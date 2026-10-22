using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>Frozen barrier configs of protocol equity-base-rates-protocol-v1 (spec §5). Fractions.</summary>
public static class EquityBarrierConfigs
{
    public sealed record Config(string ConfigId, double TargetPct, double StopPct, int HorizonSessions);

    public static readonly IReadOnlyList<Config> All =
    [
        new("tb-p5-s4-h20", 0.05, 0.04, 20),
        new("tb-p10-s6-h60", 0.10, 0.06, 60),
    ];
}

/// <summary>
/// Reads and verifies the quant base-rates artifact written by ai/equity_base_rates.py
/// (<c>{EvidenceCatalog:RootPath}/equity-base-rates/{SYMBOL}/base-rates.json</c>, schema
/// equity-base-rates-artifact-v1) and serves GET /api/equities/{symbol}/base-rates
/// (equity-base-rates-v1, spec §5/§11). Fail-closed by construction:
/// - only the requested symbol's own file is ever read (never a fallback to another symbol);
/// - size cap + strict JsonValueKind guards before every access (AGENTS pitfall #17);
/// - schemaVersion / symbol / timeframe / priceBasis must match the catalog;
/// - the dataset fingerprint (count, first, last, maxUpdatedAtUtc at microsecond precision over
///   stored 1d bars ≤ lastOpenTimeMs) must match the DB, else <c>artifact-stale-revision</c>;
/// - dataAsOfOpenTimeMs must be the latest stored finalized 1d bar, else <c>artifact-stale-asof</c>;
/// - <c>decisionUse</c> is non-null only when gatePassed ∧ fresh ∧ fingerprint ok.
/// Also proxies the admin rebuild to the AI service.
/// </summary>
public sealed class EquityBaseRatesService
{
    public const string ArtifactSchemaVersion = "equity-base-rates-artifact-v1";
    public const string DefaultMethodVersion = "equity-knn-v1";
    public const string FingerprintTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public const string ReasonMissing = "artifact-missing";
    public const string ReasonTooLarge = "artifact-too-large";
    public const string ReasonUnreadable = "artifact-unreadable";
    public const string ReasonInvalid = "artifact-invalid";
    public const string ReasonSchemaMismatch = "artifact-schema-mismatch";
    public const string ReasonSymbolMismatch = "artifact-symbol-mismatch";
    public const string ReasonTimeframeMismatch = "artifact-timeframe-mismatch";
    public const string ReasonPriceBasisMismatch = "artifact-price-basis-mismatch";
    public const string ReasonStaleRevision = "artifact-stale-revision";
    public const string ReasonStaleAsOf = "artifact-stale-asof";
    public const string ReasonGateNotPassed = "gate-not-passed";
    public const string ReasonCurrentNotLatest = "current-signal-not-latest";

    private readonly AppDbContext _db;
    private readonly string _rootPath;
    private readonly long _maxBytes;
    private readonly VietnamMarketCalendar _calendar;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<EquityBaseRatesService> _logger;

    public EquityBaseRatesService(
        AppDbContext db,
        IOptions<EvidenceCatalogOptions> options,
        IHostEnvironment environment,
        VietnamMarketCalendar calendar,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<EquityBaseRatesService> logger,
        InstrumentCatalog? catalog = null)
        : this(db, ResolveRoot(options.Value.RootPath, environment.ContentRootPath),
            Math.Clamp(options.Value.MaxArtifactBytes, 1_024, 32L * 1024 * 1024),
            calendar, httpClientFactory, timeProvider, logger, catalog)
    {
    }

    internal EquityBaseRatesService(
        AppDbContext db,
        string rootPath,
        long maxBytes,
        VietnamMarketCalendar calendar,
        IHttpClientFactory? httpClientFactory = null,
        TimeProvider? timeProvider = null,
        ILogger<EquityBaseRatesService>? logger = null,
        InstrumentCatalog? catalog = null)
    {
        _db = db;
        _rootPath = Path.GetFullPath(rootPath);
        _maxBytes = maxBytes;
        _calendar = calendar;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityBaseRatesService>.Instance;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    private static string ResolveRoot(string configured, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("EvidenceCatalog:RootPath is required.");
        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }

    /// <summary>Artifact path for a catalog symbol (canonical, so no traversal is possible).</summary>
    internal string ArtifactPath(string canonicalSymbol) =>
        Path.Combine(_rootPath, "equity-base-rates", canonicalSymbol, "base-rates.json");

    internal sealed record ConfigEvaluation(
        string ConfigId,
        double TargetPct,
        double StopPct,
        int HorizonSessions,
        bool GatePassed,
        long? CurrentSignalBarOpenTimeMs,
        double? ProbTargetFirst,
        double? ProbStopFirst,
        string? AbstainReason);

    /// <summary>Response + the facts the forecast journal needs to record quant rows.</summary>
    internal sealed record Evaluation(
        EquityBaseRatesResponse Response,
        bool Usable,
        bool Fresh,
        string InputSha256,
        string? SchemaVersion,
        string MethodVersion,
        IReadOnlyList<ConfigEvaluation> Configs);

    public async Task<EquityBaseRatesResponse> GetAsync(string symbol, CancellationToken ct = default) =>
        (await EvaluateAsync(symbol, ct)).Response;

    internal async Task<Evaluation> EvaluateAsync(string symbol, CancellationToken ct = default)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        var sym = instrument.Symbol;
        var path = ArtifactPath(sym);

        if (!File.Exists(path))
            return Unavailable(sym, ReasonMissing, EmptySha());
        var info = new FileInfo(path);
        if (info.Length > _maxBytes)
            return Unavailable(sym, ReasonTooLarge, EmptySha());

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, ct); }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Base-rates artifact for {Symbol} could not be read", sym);
            return Unavailable(sym, ReasonUnreadable, EmptySha());
        }
        if (bytes.LongLength > _maxBytes)
            return Unavailable(sym, ReasonTooLarge, EmptySha());
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException)
        {
            return Unavailable(sym, ReasonUnreadable, sha);
        }

        using (document)
        {
            ParsedArtifact parsed;
            try
            {
                parsed = ParseArtifact(document.RootElement);
            }
            catch (ArtifactShapeException ex)
            {
                return Unavailable(sym, ex.Reason, sha, ex.Message);
            }

            if (parsed.SchemaVersion != ArtifactSchemaVersion)
                return Unavailable(sym, ReasonSchemaMismatch, sha);
            if (parsed.Symbol != sym)
                return Unavailable(sym, ReasonSymbolMismatch, sha);
            if (parsed.Timeframe != EquityCandleTimeframes.Day)
                return Unavailable(sym, ReasonTimeframeMismatch, sha);
            if (parsed.PriceBasis != instrument.PriceBasis)
                return Unavailable(sym, ReasonPriceBasisMismatch, sha);

            var staleReasons = new List<string>();
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (!await FingerprintMatchesAsync(sym, parsed.Dataset, nowMs, ct))
                staleReasons.Add(ReasonStaleRevision);
            var latest = await LatestFinalizedDailyOpenAsync(sym, nowMs, ct);
            if (latest is null || parsed.DataAsOfOpenTimeMs != latest.Value)
                staleReasons.Add(ReasonStaleAsOf);
            var fresh = staleReasons.Count == 0;

            var configs = new List<EquityBaseRatesConfigDto>();
            var evaluations = new List<ConfigEvaluation>();
            foreach (var c in parsed.Configs)
            {
                var reasons = new List<string>(staleReasons);
                var hasProbs = c.Current.ProbTargetFirst is not null && c.Current.ProbStopFirst is not null
                    && c.Current.ProbTimeout is not null && c.Current.SignalBarOpenTimeMs is not null
                    && !c.Current.Abstained;
                if (!hasProbs)
                    reasons.Add(c.Current.AbstainReason ?? "current-abstained");
                else if (latest is not null && c.Current.SignalBarOpenTimeMs != latest)
                    reasons.Add(ReasonCurrentNotLatest);
                var abstained = reasons.Count > 0;

                var usableForDecision = !abstained && fresh && c.Validation.GatePassed;
                var research = hasProbs
                    ? new EquityBaseRatesResearch(
                        usableForDecision ? EquityResearchLabels.Validated : EquityResearchLabels.Unvalidated,
                        c.Current.SignalBarOpenTimeMs!.Value,
                        c.Current.ProbTargetFirst!.Value, c.Current.ProbStopFirst!.Value, c.Current.ProbTimeout!.Value,
                        c.Current.EvNetFraction, c.Current.K, c.Current.NEligible, c.Current.Prior)
                    : null;
                var decisionUse = usableForDecision
                    ? new EquityBaseRatesDecisionUse(c.Current.ProbTargetFirst!.Value, c.Current.ProbStopFirst!.Value,
                        c.Current.ProbTimeout!.Value, c.Current.EvNetFraction)
                    : null;
                configs.Add(new EquityBaseRatesConfigDto(c.ConfigId, c.TargetPct, c.StopPct, c.HorizonSessions,
                    abstained, reasons, decisionUse, research, c.Validation));
                evaluations.Add(new ConfigEvaluation(c.ConfigId, c.TargetPct, c.StopPct, c.HorizonSessions,
                    c.Validation.GatePassed,
                    hasProbs ? c.Current.SignalBarOpenTimeMs : null,
                    hasProbs && !abstained ? c.Current.ProbTargetFirst : null,
                    hasProbs && !abstained ? c.Current.ProbStopFirst : null,
                    abstained ? string.Join(";", reasons) : null));
            }

            var topReasons = new List<string>(staleReasons);
            var anyDecision = configs.Any(c => c.DecisionUse is not null);
            if (!anyDecision && fresh)
            {
                if (configs.Any(c => !c.Abstained && c.Validation is { GatePassed: false }))
                    topReasons.Add(ReasonGateNotPassed);
                foreach (var r in configs.Where(c => c.Abstained).SelectMany(c => c.AbstainReasons).Distinct())
                    if (!topReasons.Contains(r)) topReasons.Add(r);
            }
            var status = anyDecision ? EquityBaseRatesStatuses.Ok : EquityBaseRatesStatuses.Abstained;
            var response = new EquityBaseRatesResponse(
                EquityBaseRatesResponse.Version, sym, EquityCandleTimeframes.Day, status, topReasons,
                new EquityBaseRatesArtifactInfo(parsed.GeneratedAtUtc, parsed.DataAsOfOpenTimeMs, parsed.PriceBasis,
                    parsed.MethodVersion, parsed.ProtocolVersion, parsed.Dataset.Sha256),
                configs);
            return new Evaluation(response, true, fresh, sha, parsed.SchemaVersion, parsed.MethodVersion, evaluations);
        }
    }

    private static string EmptySha() => Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();

    private Evaluation Unavailable(string symbol, string reason, string sha, string? detail = null)
    {
        if (detail is not null)
            _logger.LogWarning("Base-rates artifact for {Symbol} rejected: {Reason} ({Detail})", symbol, reason, detail);
        var response = new EquityBaseRatesResponse(EquityBaseRatesResponse.Version, symbol, EquityCandleTimeframes.Day,
            EquityBaseRatesStatuses.Unavailable, [reason], null, []);
        var configs = EquityBarrierConfigs.All.Select(c => new ConfigEvaluation(c.ConfigId, c.TargetPct, c.StopPct,
            c.HorizonSessions, false, null, null, null, reason)).ToArray();
        return new Evaluation(response, false, false, sha, null, DefaultMethodVersion, configs);
    }

    /// <summary>Recompute the fingerprint over stored 1d bars ≤ lastOpenTimeMs (closed by now).</summary>
    private async Task<bool> FingerprintMatchesAsync(string symbol, ParsedDataset dataset, long nowMs, CancellationToken ct)
    {
        var rows = _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == EquityCandleTimeframes.Day
                        && c.OpenTimeMs <= dataset.LastOpenTimeMs && c.CloseTimeMs <= nowMs);
        var count = await rows.LongCountAsync(ct);
        if (count != dataset.BarCount || count == 0) return false;
        var first = await rows.MinAsync(c => c.OpenTimeMs, ct);
        var last = await rows.MaxAsync(c => c.OpenTimeMs, ct);
        var maxUpdated = await rows.MaxAsync(c => c.UpdatedAtUtc, ct);
        return first == dataset.FirstOpenTimeMs
            && last == dataset.LastOpenTimeMs
            && string.Equals(FormatFingerprintTimestamp(maxUpdated), dataset.MaxUpdatedAtUtc, StringComparison.Ordinal);
    }

    /// <summary>Microsecond UTC timestamp exactly as Postgres stores it and Python formats it.</summary>
    internal static string FormatFingerprintTimestamp(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        // Truncate to whole microseconds (Postgres precision) before formatting.
        utc = new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
        return utc.ToString(FingerprintTimestampFormat, CultureInfo.InvariantCulture);
    }

    internal async Task<long?> LatestFinalizedDailyOpenAsync(string symbol, long nowMs, CancellationToken ct)
    {
        var recent = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == EquityCandleTimeframes.Day && c.CloseTimeMs <= nowMs)
            .OrderByDescending(c => c.OpenTimeMs)
            .Select(c => c.OpenTimeMs)
            .Take(5)
            .ToListAsync(ct);
        foreach (var open in recent)
            if (_calendar.IsFinalized(EquityCandleTimeframes.Day, open, nowMs))
                return open;
        return null;
    }

    // --- strict parsing -----------------------------------------------------------------

    private sealed class ArtifactShapeException(string reason, string message) : Exception(message)
    {
        public string Reason { get; } = reason;
    }

    private sealed record ParsedDataset(long BarCount, long FirstOpenTimeMs, long LastOpenTimeMs, string? MaxUpdatedAtUtc, string Sha256);

    private sealed record ParsedCurrent(
        long? SignalBarOpenTimeMs, double? ProbTargetFirst, double? ProbStopFirst, double? ProbTimeout,
        double? EvNetFraction, int? K, int NEligible, EquityBaseRatesPrior Prior, bool Abstained, string? AbstainReason);

    private sealed record ParsedConfig(
        string ConfigId, double TargetPct, double StopPct, int HorizonSessions,
        EquityBaseRatesValidation Validation, ParsedCurrent Current);

    private sealed record ParsedArtifact(
        string? SchemaVersion, string? Symbol, string? Timeframe, string? PriceBasis, string GeneratedAtUtc,
        long DataAsOfOpenTimeMs, string MethodVersion, string ProtocolVersion, ParsedDataset Dataset,
        IReadOnlyList<ParsedConfig> Configs);

    private static ParsedArtifact ParseArtifact(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid("root is not an object");
        // Identity fields are read leniently so a mismatch is reported precisely (schema/symbol/…).
        var schema = OptString(root, "schemaVersion");
        if (schema != ArtifactSchemaVersion)
            return new ParsedArtifact(schema, null, null, null, "", 0, "", "", new ParsedDataset(0, 0, 0, null, ""), []);
        var dataset = ReqObject(root, "dataset");
        var method = ReqObject(root, "method");
        var protocol = ReqObject(root, "protocol");
        var configsEl = ReqArray(root, "configs");
        var configs = new List<ParsedConfig>();
        foreach (var c in configsEl.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) throw Invalid("configs[] item is not an object");
            configs.Add(ParseConfig(c));
        }
        return new ParsedArtifact(
            schema,
            OptString(root, "symbol"),
            OptString(root, "timeframe"),
            OptString(root, "priceBasis"),
            ReqString(root, "generatedAtUtc"),
            ReqLong(root, "dataAsOfOpenTimeMs"),
            ReqString(method, "version"),
            ReqString(protocol, "version"),
            new ParsedDataset(
                ReqLong(dataset, "barCount"),
                ReqLong(dataset, "firstOpenTimeMs"),
                ReqLong(dataset, "lastOpenTimeMs"),
                OptString(dataset, "maxUpdatedAtUtc", allowNull: true),
                ReqString(dataset, "sha256")),
            configs);
    }

    private static ParsedConfig ParseConfig(JsonElement c)
    {
        var v = ReqObject(c, "validation");
        var cur = ReqObject(c, "current");
        var bins = new List<EquityCalibrationBin>();
        if (v.TryGetProperty("calibrationBins", out var binsEl) && binsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in binsEl.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) throw Invalid("calibrationBins[] item is not an object");
                bins.Add(new EquityCalibrationBin(ReqDouble(b, "lower"), ReqDouble(b, "upper"), (int)ReqLong(b, "n"),
                    OptDouble(b, "meanPredicted"), OptDouble(b, "observedRate")));
            }
        }
        IReadOnlyList<double>? ci = null;
        if (v.TryGetProperty("brierDiffCi90", out var ciEl) && ciEl.ValueKind == JsonValueKind.Array)
        {
            var values = ciEl.EnumerateArray().ToArray();
            if (values.Length == 2 && values.All(x => x.ValueKind == JsonValueKind.Number))
                ci = [values[0].GetDouble(), values[1].GetDouble()];
            else if (values.Length != 2)
                throw Invalid("brierDiffCi90 must be [lower, upper]");
        }
        var gateReasons = new List<string>();
        if (v.TryGetProperty("gateReasons", out var gr) && gr.ValueKind == JsonValueKind.Array)
            foreach (var r in gr.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) gateReasons.Add(r.GetString()!);
        var gatePassed = v.TryGetProperty("gatePassed", out var gp) && gp.ValueKind == JsonValueKind.True;
        if (!v.TryGetProperty("gatePassed", out gp) || gp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("validation.gatePassed must be a boolean");

        var validation = new EquityBaseRatesValidation(
            gatePassed, gateReasons, OptInt(v, "nOos"), OptInt(v, "nEffective"),
            OptDouble(v, "brierKnn"), OptDouble(v, "brierPrior"), OptDouble(v, "brierDiffMean"), ci,
            OptDouble(v, "ece"), bins, OptDouble(v, "coverage"), OptDouble(v, "abstentionRate"),
            OptDouble(v, "evNetOosMean"), OptDouble(v, "evNetOosWhenPositive"));

        var prior = cur.TryGetProperty("prior", out var pr) && pr.ValueKind == JsonValueKind.Object
            ? new EquityBaseRatesPrior(OptDouble(pr, "probTargetFirst"), OptDouble(pr, "probStopFirst"), OptDouble(pr, "probTimeout"))
            : new EquityBaseRatesPrior(null, null, null);
        var abstained = !cur.TryGetProperty("abstained", out var ab) || ab.ValueKind != JsonValueKind.False;
        var current = new ParsedCurrent(
            OptLong(cur, "signalBarOpenTimeMs"),
            OptProbability(cur, "probTargetFirst"), OptProbability(cur, "probStopFirst"), OptProbability(cur, "probTimeout"),
            OptDouble(cur, "evNetFraction"), OptInt(cur, "k"), OptInt(cur, "nEligible") ?? 0, prior,
            abstained, OptString(cur, "abstainReason", allowNull: true));
        return new ParsedConfig(ReqString(c, "configId"), ReqDouble(c, "targetPct"), ReqDouble(c, "stopPct"),
            (int)ReqLong(c, "horizonSessions"), validation, current);
    }

    private static ArtifactShapeException Invalid(string message) => new(ReasonInvalid, message);

    private static JsonElement ReqObject(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : throw Invalid($"{name} must be an object");

    private static JsonElement ReqArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : throw Invalid($"{name} must be an array");

    private static string ReqString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()! : throw Invalid($"{name} must be a non-empty string");

    private static string? OptString(JsonElement obj, string name, bool allowNull = false)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Null && allowNull) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        throw Invalid($"{name} must be a string");
    }

    private static long ReqLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : throw Invalid($"{name} must be an integer");

    private static long? OptLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : throw Invalid($"{name} must be an integer");
    }

    private static int? OptInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : throw Invalid($"{name} must be an integer");
    }

    private static double ReqDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble())
            ? v.GetDouble() : throw Invalid($"{name} must be a number");

    private static double? OptDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : throw Invalid($"{name} must be a number");
    }

    private static double? OptProbability(JsonElement obj, string name)
    {
        var p = OptDouble(obj, name);
        return p is null or (>= 0 and <= 1) ? p : throw Invalid($"{name} must be within [0, 1]");
    }

    // --- admin rebuild (AI proxy) -------------------------------------------------------

    public sealed record RebuildResult(int StatusCode, string? Code, string? Message, JsonElement? Summary);

    /// <summary>POST AI /api/equity-base-rates/build?symbol=; never throws for upstream failures.</summary>
    public async Task<RebuildResult> RebuildAsync(string symbol, CancellationToken ct = default)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        if (_httpClientFactory is null)
            return new RebuildResult(StatusCodes.Status503ServiceUnavailable, "BASE_RATES_SOURCE_UNAVAILABLE",
                "AI service client is not configured.", null);
        try
        {
            var client = _httpClientFactory.CreateClient("AIService");
            using var response = await client.PostAsync(
                $"/api/equity-base-rates/build?symbol={Uri.EscapeDataString(instrument.Symbol)}", content: null, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonElement? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                parsed = doc.RootElement.Clone();
            }
            catch (JsonException) { }

            if (response.IsSuccessStatusCode && parsed is { ValueKind: JsonValueKind.Object })
                return new RebuildResult(StatusCodes.Status200OK, null, null, parsed);

            string? reason = null, message = null;
            if (parsed is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            {
                if (detail.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String) reason = r.GetString();
                if (detail.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
            }
            var status = (int)response.StatusCode;
            return status is 400 or 422
                ? new RebuildResult(StatusCodes.Status422UnprocessableEntity, "BASE_RATES_BUILD_REJECTED",
                    $"AI refused the build ({status}): {reason ?? "unknown"}{(message is null ? "" : $" - {message}")}", null)
                : new RebuildResult(StatusCodes.Status502BadGateway, "BASE_RATES_SOURCE_UNAVAILABLE",
                    $"AI base-rates build failed with HTTP {status}{(reason is null ? "" : $" ({reason})")}.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AI base-rates build unreachable for {Symbol}", instrument.Symbol);
            return new RebuildResult(StatusCodes.Status503ServiceUnavailable, "BASE_RATES_SOURCE_UNAVAILABLE",
                "The AI base-rates service is unavailable.", null);
        }
    }
}
