using Backend.Services.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Services;

public enum CurrentConditionsOutcome
{
    Ok,
    SourceUnavailable,
    PayloadInvalid
}

public sealed record CurrentConditionsResult(CurrentConditionsOutcome Outcome, JsonObject? Payload);

/// <summary>
/// Proxies the AI service <c>GET /api/current-conditions</c> endpoint and joins
/// every returned condition with the verified technical-event statistical report
/// that <see cref="IResearchEvidenceCatalog"/> already hash-verified and cached.
/// Implements the backend half of
/// ai/docs/research/current-conditions-contract.md §3.
/// </summary>
public sealed class CurrentConditionsService
{
    internal const string ConditionsSchemaVersion = "current-conditions-v1";
    internal const string StatisticalEvidenceSchema = "btc-technical-evidence-statistics/v1";

    private static readonly string[] StatisticalMetrics = ["forwardReturn", "mfe", "mae"];
    private static readonly int[] StatisticalHorizons = [1, 3, 6];
    private static readonly HashSet<string> ValidKinds = new(StringComparer.Ordinal)
        { "triggeredOnBar", "state", "activeZone", "operativeLeg" };
    private static readonly HashSet<string> ValidDirections = new(StringComparer.Ordinal)
        { "bullish", "bearish", "neutral" };
    private static readonly IReadOnlyDictionary<HypothesisKey, JsonElement> EmptyIndex =
        new Dictionary<HypothesisKey, JsonElement>();

    private readonly HttpClient _aiClient;
    private readonly IResearchEvidenceCatalog _catalog;
    private readonly ILogger<CurrentConditionsService> _logger;

    public CurrentConditionsService(
        IHttpClientFactory httpClientFactory,
        IResearchEvidenceCatalog catalog,
        ILogger<CurrentConditionsService> logger)
    {
        _aiClient = httpClientFactory.CreateClient("AIService");
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<CurrentConditionsResult> GetAsync(string timeframe, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            using var response = await _aiClient.GetAsync(
                $"/api/current-conditions?timeframe={timeframe}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AI current-conditions returned {StatusCode} for {Timeframe}.",
                    (int)response.StatusCode, timeframe);
                return new CurrentConditionsResult(CurrentConditionsOutcome.SourceUnavailable, null);
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "AI current-conditions returned non-JSON content for {Timeframe}.", timeframe);
                return new CurrentConditionsResult(CurrentConditionsOutcome.PayloadInvalid, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller aborted the request; do not convert it into an upstream error.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI current-conditions request failed for {Timeframe}.", timeframe);
            return new CurrentConditionsResult(CurrentConditionsOutcome.SourceUnavailable, null);
        }

        using (document)
        {
            var validationError = ValidatePayload(document.RootElement, timeframe);
            if (validationError is not null)
            {
                _logger.LogWarning(
                    "AI current-conditions payload failed validation for {Timeframe}: {Reason}",
                    timeframe, validationError);
                return new CurrentConditionsResult(CurrentConditionsOutcome.PayloadInvalid, null);
            }
            return new CurrentConditionsResult(
                CurrentConditionsOutcome.Ok, BuildResponse(document.RootElement, timeframe));
        }
    }

    private JsonObject BuildResponse(JsonElement root, string timeframe)
    {
        var asOfMs = root.GetProperty("asOfMs").GetInt64();
        var evidence = LoadEvidence(timeframe, ReadString(root.GetProperty("contract"), "definitionsSha256"));
        var passers = new List<EvidencePasser>();
        var conditions = new JsonArray();
        foreach (var element in root.GetProperty("conditions").EnumerateArray())
        {
            var node = JsonNode.Parse(element.GetRawText())!.AsObject();
            var module = element.GetProperty("module").GetString()!;
            var eventType = element.GetProperty("eventType").GetString()!;
            var direction = element.GetProperty("direction").GetString()!;
            node["evidence"] = BuildConditionEvidence(evidence, module, eventType, direction, passers);
            conditions.Add(node);
        }

        var conflicts = new JsonArray();
        foreach (var horizon in StatisticalHorizons)
        {
            var bullish = OrderedKeys(passers, horizon, "bullish");
            var bearish = OrderedKeys(passers, horizon, "bearish");
            if (bullish.Length == 0 || bearish.Length == 0)
                continue;
            conflicts.Add(new JsonObject
            {
                ["horizon"] = horizon,
                ["metric"] = "forwardReturn",
                ["bullish"] = new JsonArray(bullish.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["bearish"] = new JsonArray(bearish.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
            });
        }

        return new JsonObject
        {
            ["timeframe"] = timeframe,
            ["asOfMs"] = asOfMs,
            ["latestClosedBar"] = Clone(root.GetProperty("latestClosedBar")),
            ["analysisWindow"] = Clone(root.GetProperty("analysisWindow")),
            ["contract"] = Clone(root.GetProperty("contract")),
            ["conditions"] = conditions,
            ["evidence"] = BuildEvidenceEnvelope(evidence, asOfMs, timeframe),
            ["conflicts"] = conflicts,
            ["warnings"] = root.TryGetProperty("warnings", out var warnings) ? Clone(warnings) : new JsonArray(),
            ["unavailableModules"] = root.TryGetProperty("unavailableModules", out var modules) ? Clone(modules) : new JsonArray(),
            ["generatedAtMs"] = Clone(root.GetProperty("generatedAtMs"))
        };
    }

    private EvidenceContext LoadEvidence(string timeframe, string? conditionsContractSha256)
    {
        ResearchEvidenceCatalogResponse catalog;
        try
        {
            catalog = _catalog.GetCatalog();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Research evidence catalog load failed.");
            return EvidenceContext.Unavailable("evidence-catalog-unavailable");
        }

        var item = catalog.Items
            .Where(x => string.Equals(x.Kind, "event", StringComparison.Ordinal)
                && string.Equals(x.EvidenceTier, "descriptive", StringComparison.Ordinal)
                && string.Equals(x.Timeframe, timeframe, StringComparison.Ordinal)
                && x.Integrity.Verified)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
        if (item is null)
            return EvidenceContext.Unavailable("no-verified-evidence-bundle");

        ResearchEvidenceDetailDto? detail;
        try
        {
            detail = _catalog.GetDetail(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Research evidence detail load failed for {Id}.", item.Id);
            return EvidenceContext.Unavailable("no-verified-evidence-bundle", item.Id, item.ManifestSha256);
        }
        if (detail is null)
            return EvidenceContext.Unavailable("no-verified-evidence-bundle", item.Id, item.ManifestSha256);

        var cutoffMs = detail.Dataset?.LastDecisionTimeMs;
        if (detail.StatisticalEvidence is not { } statistics
            || statistics.ValueKind != JsonValueKind.Object
            || !string.Equals(ReadString(statistics, "schema"), StatisticalEvidenceSchema, StringComparison.Ordinal)
            || !statistics.TryGetProperty("hypotheses", out var hypothesesElement)
            || hypothesesElement.ValueKind != JsonValueKind.Array)
        {
            return EvidenceContext.Unavailable(
                "statistical-evidence-unavailable", item.Id, item.ManifestSha256, null, cutoffMs);
        }

        var specSha256 = ReadString(statistics, "specSha256");
        if (!TryGetObject(statistics, "scope", out var scope)
            || !string.Equals(ReadString(scope, "timeframe"), timeframe, StringComparison.Ordinal))
        {
            return EvidenceContext.Unavailable(
                "evidence-scope-mismatch", item.Id, item.ManifestSha256, specSha256, cutoffMs);
        }

        var reportContractSha256 = TryGetObject(statistics, "declaredFamily", out var family)
            ? ReadString(family, "technicalModuleContractDefinitionsSha256")
            : null;
        if (reportContractSha256 is null)
        {
            return EvidenceContext.Unavailable(
                "evidence-contract-hash-unverifiable", item.Id, item.ManifestSha256, specSha256, cutoffMs);
        }
        if (conditionsContractSha256 is not null
            && !string.Equals(conditionsContractSha256, reportContractSha256, StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceContext.Unavailable(
                "contract-definitions-sha-mismatch", item.Id, item.ManifestSha256, specSha256, cutoffMs);
        }

        var index = new Dictionary<HypothesisKey, JsonElement>();
        foreach (var hypothesis in hypothesesElement.EnumerateArray())
        {
            if (hypothesis.ValueKind != JsonValueKind.Object)
                continue;
            var module = ReadString(hypothesis, "module");
            var eventType = ReadString(hypothesis, "eventType");
            var metric = ReadString(hypothesis, "metric");
            if (module is null || eventType is null || metric is null
                || !hypothesis.TryGetProperty("horizonBars", out var horizonElement)
                || horizonElement.ValueKind != JsonValueKind.Number
                || !horizonElement.TryGetInt32(out var horizon))
                continue;
            index.TryAdd(new HypothesisKey(module, eventType, horizon, metric), hypothesis);
        }
        return new EvidenceContext(true, null, item.Id, item.ManifestSha256, specSha256, cutoffMs, index);
    }

    private static JsonObject BuildEvidenceEnvelope(EvidenceContext evidence, long asOfMs, string timeframe)
    {
        long? ageBars = evidence.CutoffMs is { } cutoff
            ? (long)Math.Floor((asOfMs - cutoff) / (double)IntervalMs(timeframe))
            : null;
        return new JsonObject
        {
            ["available"] = evidence.Available,
            ["reason"] = ToNode(evidence.Reason),
            ["runId"] = ToNode(evidence.RunId),
            ["manifestSha256"] = ToNode(evidence.ManifestSha256),
            ["specSha256"] = ToNode(evidence.SpecSha256),
            ["cutoffMs"] = ToNode(evidence.CutoffMs),
            ["evidenceAgeBars"] = ToNode(ageBars)
        };
    }

    private static JsonObject BuildConditionEvidence(
        EvidenceContext evidence,
        string module,
        string eventType,
        string direction,
        List<EvidencePasser> passers)
    {
        var result = new JsonObject();
        foreach (var horizon in StatisticalHorizons)
        {
            var cells = new JsonObject();
            foreach (var metric in StatisticalMetrics)
                cells[metric] = BuildCell(evidence, module, eventType, direction, horizon, metric, passers);
            result[horizon.ToString(CultureInfo.InvariantCulture)] = cells;
        }
        return result;
    }

    private static JsonObject BuildCell(
        EvidenceContext evidence,
        string module,
        string eventType,
        string direction,
        int horizon,
        string metric,
        List<EvidencePasser> passers)
    {
        if (!evidence.Available)
            return UntestedCell("evidence-unavailable");
        if (!evidence.Hypotheses.TryGetValue(new HypothesisKey(module, eventType, horizon, metric), out var hypothesis))
            return UntestedCell("untested");
        var status = ReadString(hypothesis, "status");
        if (!string.Equals(status, "tested", StringComparison.Ordinal))
        {
            return UntestedCell(string.Equals(status, "insufficient_or_no_matched_sample", StringComparison.Ordinal)
                ? "insufficient-sample"
                : "untested");
        }

        var passesDeclaredFdr = hypothesis.TryGetProperty("passesDeclaredFdr", out var fdr)
            && fdr.ValueKind == JsonValueKind.True;
        var sufficientSample = hypothesis.TryGetProperty("sufficientSample", out var sample)
            && sample.ValueKind == JsonValueKind.True;
        // A conflict passer must clear both declared gates — FDR alone is not
        // enough when the non-overlapping sample is below the declared minimum.
        if (passesDeclaredFdr && sufficientSample
            && string.Equals(metric, "forwardReturn", StringComparison.Ordinal)
            && direction is "bullish" or "bearish")
        {
            passers.Add(new EvidencePasser(horizon, direction, $"{module}:{eventType}"));
        }

        return new JsonObject
        {
            ["tested"] = true,
            ["rawP"] = Scalar(hypothesis, "rawPValue"),
            ["adjustedQValue"] = Scalar(hypothesis, "adjustedQValue"),
            ["passesDeclaredFdr"] = Scalar(hypothesis, "passesDeclaredFdr"),
            ["sufficientSample"] = Scalar(hypothesis, "sufficientSample"),
            ["nonOverlappingPairs"] = Scalar(hypothesis, "sampleDiagnostics", "maximumGreedyNonOverlappingOutcomeWindows"),
            ["effect"] = Scalar(hypothesis, "effectSize", "pairedStandardizedMeanDifference"),
            ["ciLower"] = Scalar(hypothesis, "blockBootstrap", "meanDifferenceInterval", "lower"),
            ["ciUpper"] = Scalar(hypothesis, "blockBootstrap", "meanDifferenceInterval", "upper"),
            ["meanPairedDifference"] = Scalar(hypothesis, "effectSize", "meanPairedDifference")
        };
    }

    private static JsonObject UntestedCell(string reason) => new()
    {
        ["tested"] = false,
        ["reason"] = reason
    };

    private static string[] OrderedKeys(List<EvidencePasser> passers, int horizon, string direction) =>
        passers.Where(x => x.Horizon == horizon && string.Equals(x.Direction, direction, StringComparison.Ordinal))
            .Select(x => x.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Returns null when the payload conforms to the frozen schema, else a reason.</summary>
    internal static string? ValidatePayload(JsonElement root, string timeframe)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "root is not an object";
        if (!string.Equals(ReadString(root, "schemaVersion"), ConditionsSchemaVersion, StringComparison.Ordinal))
            return "schemaVersion is missing or unsupported";
        if (!string.Equals(ReadString(root, "symbol"), "BTCUSDT", StringComparison.Ordinal))
            return "symbol is missing or unsupported";
        if (!string.Equals(ReadString(root, "timeframe"), timeframe, StringComparison.Ordinal))
            return "timeframe is missing or does not match the request";
        if (!IsInt64(root, "asOfMs") || !IsNumber(root, "generatedAtMs"))
            return "asOfMs or generatedAtMs is missing";
        if (!TryGetObject(root, "latestClosedBar", out var bar)
            || !AllNumbers(bar, "openTimeMs", "closeTimeMs", "open", "high", "low", "close", "volume"))
            return "latestClosedBar is missing or malformed";
        if (!TryGetObject(root, "analysisWindow", out var window)
            || !IsNumber(window, "firstOpenTimeMs") || !IsNumber(window, "bars")
            || !IsBoolean(window, "contiguous") || !IsNumber(window, "windowBars"))
            return "analysisWindow is missing or malformed";
        if (!TryGetObject(root, "contract", out var contract)
            || ReadString(contract, "contractVersion") is null
            || ReadString(contract, "definitionsSha256") is null
            || ReadString(contract, "rawFileSha256") is null)
            return "contract is missing or malformed";
        if (!root.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array)
            return "conditions is missing or not an array";
        var index = 0;
        foreach (var condition in conditions.EnumerateArray())
        {
            var kind = ReadString(condition, "kind");
            var direction = ReadString(condition, "direction");
            if (condition.ValueKind != JsonValueKind.Object
                || ReadString(condition, "module") is null
                || ReadString(condition, "eventType") is null
                || kind is null || !ValidKinds.Contains(kind)
                || direction is null || !ValidDirections.Contains(direction)
                || !IsNumber(condition, "formedTimeMs")
                || !IsNumber(condition, "availableTimeMs"))
                return $"conditions[{index}] is malformed";
            if (condition.TryGetProperty("eventId", out var eventId)
                && eventId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return $"conditions[{index}].eventId is malformed";
            if (condition.TryGetProperty("context", out var context) && context.ValueKind != JsonValueKind.Object)
                return $"conditions[{index}].context is malformed";
            if (condition.TryGetProperty("details", out var details) && details.ValueKind != JsonValueKind.Object)
                return $"conditions[{index}].details is malformed";
            index++;
        }
        if (root.TryGetProperty("warnings", out var warnings)
            && (warnings.ValueKind != JsonValueKind.Array
                || warnings.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)))
            return "warnings is malformed";
        if (root.TryGetProperty("unavailableModules", out var unavailableModules)
            && (unavailableModules.ValueKind != JsonValueKind.Array
                || unavailableModules.EnumerateArray().Any(x =>
                    x.ValueKind != JsonValueKind.Object || ReadString(x, "module") is null)))
            return "unavailableModules is malformed";
        return null;
    }

    private static long IntervalMs(string timeframe) => timeframe switch
    {
        "1h" => 3_600_000L,
        "4h" => 14_400_000L,
        "1d" => 86_400_000L,
        _ => 1L
    };

    private static JsonNode? Clone(JsonElement element) => JsonNode.Parse(element.GetRawText());

    private static JsonNode? Scalar(JsonElement source, params string[] path)
    {
        var current = source;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
                return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(current.GetRawText());
    }

    private static JsonNode? ToNode(string? value) => value is null ? null : JsonValue.Create(value);
    private static JsonNode? ToNode(long? value) => value is null ? null : JsonValue.Create(value.Value);

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool TryGetObject(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static bool IsNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number;

    private static bool IsInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out _);

    private static bool IsBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool AllNumbers(JsonElement element, params string[] properties) =>
        properties.All(property => IsNumber(element, property));

    private readonly record struct HypothesisKey(string Module, string EventType, int Horizon, string Metric);

    private readonly record struct EvidencePasser(int Horizon, string Direction, string Key);

    private sealed record EvidenceContext(
        bool Available,
        string? Reason,
        string? RunId,
        string? ManifestSha256,
        string? SpecSha256,
        long? CutoffMs,
        IReadOnlyDictionary<HypothesisKey, JsonElement> Hypotheses)
    {
        public static EvidenceContext Unavailable(
            string reason,
            string? runId = null,
            string? manifestSha256 = null,
            string? specSha256 = null,
            long? cutoffMs = null) =>
            new(false, reason, runId, manifestSha256, specSha256, cutoffMs, EmptyIndex);
    }
}
