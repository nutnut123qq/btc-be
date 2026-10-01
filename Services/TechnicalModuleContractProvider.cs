using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Backend.Services;

public interface ITechnicalModuleContractProvider
{
    string ContractVersion { get; }
    /// <summary>Canonical definitions hash; goldenFixture is intentionally excluded.</summary>
    string Sha256 { get; }
    /// <summary>Hash of the exact contract artifact bytes, for file provenance only.</summary>
    string RawFileSha256 { get; }
    string CanonicalJson { get; }
    string GetCalculationVersion(string moduleKey);
}

public sealed class TechnicalModuleContractProvider : ITechnicalModuleContractProvider
{
    public const string RelativePath = "contracts/technical-module-contract.json";

    public TechnicalModuleContractProvider(IHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "contracts", "technical-module-contract.json");
        Initialize(File.ReadAllBytes(path), out var version, out var canonical, out var definitionsHash, out var rawHash,
            out var calculationVersions);
        ContractVersion = version;
        CanonicalJson = canonical;
        Sha256 = definitionsHash;
        RawFileSha256 = rawHash;
        CalculationVersions = calculationVersions;
    }

    internal TechnicalModuleContractProvider(string json)
    {
        Initialize(Encoding.UTF8.GetBytes(json), out var version, out var canonical, out var definitionsHash, out var rawHash,
            out var calculationVersions);
        ContractVersion = version;
        CanonicalJson = canonical;
        Sha256 = definitionsHash;
        RawFileSha256 = rawHash;
        CalculationVersions = calculationVersions;
    }

    public string ContractVersion { get; }
    public string Sha256 { get; }
    public string RawFileSha256 { get; }
    public string CanonicalJson { get; }
    private IReadOnlyDictionary<string, string> CalculationVersions { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string GetCalculationVersion(string moduleKey) =>
        CalculationVersions.TryGetValue(moduleKey, out var version)
            ? version
            : throw new InvalidOperationException($"Technical module contract is missing module '{moduleKey}'.");

    private static void Initialize(
        byte[] rawBytes,
        out string contractVersion,
        out string canonicalJson,
        out string definitionsHash,
        out string rawHash,
        out IReadOnlyDictionary<string, string> calculationVersions)
    {
        using var document = JsonDocument.Parse(rawBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Technical module contract root must be a JSON object.");

        contractVersion = document.RootElement.GetProperty("contractVersion").GetString()
            ?? throw new InvalidOperationException("technical module contractVersion is missing.");

        var canonical = new StringBuilder(rawBytes.Length);
        AppendCanonical(canonical, document.RootElement, isRoot: true);
        canonicalJson = canonical.ToString();
        var canonicalBytes = Encoding.UTF8.GetBytes(canonicalJson);
        definitionsHash = HexHash(canonicalBytes);
        rawHash = HexHash(rawBytes);

        if (document.RootElement.TryGetProperty("goldenFixture", out var fixture)
            && fixture.ValueKind == JsonValueKind.Object
            && fixture.TryGetProperty("expected", out var expected)
            && expected.ValueKind == JsonValueKind.Object)
            ValidateGolden(expected, definitionsHash);
        else
            throw new InvalidOperationException("Technical module contract must contain goldenFixture.expected.");

        var modules = document.RootElement.GetProperty("modules");
        if (modules.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Technical module contract modules must be an object.");
        calculationVersions = modules.EnumerateObject().ToDictionary(
            x => x.Name,
            x => x.Value.GetProperty("calculationVersion").GetString()
                ?? throw new InvalidOperationException($"Module '{x.Name}' calculationVersion is missing."),
            StringComparer.Ordinal);
    }

    private static void ValidateGolden(JsonElement expected, string definitionsHash)
    {
        static string RequiredHash(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var element)
                || element.ValueKind != JsonValueKind.String
                || element.GetString() is not { Length: 64 } hash
                || !hash.All(Uri.IsHexDigit))
                throw new InvalidOperationException($"goldenFixture.expected.{name} must be a SHA-256 hash.");
            return hash;
        }

        var expectedHash = RequiredHash(expected, "definitionsSha256");
        _ = RequiredHash(expected, "orderedEventLedgerSha256");
        _ = RequiredHash(expected, "crossLanguageSemanticLedgerSha256");
        if (!string.Equals(expectedHash, definitionsHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Technical module contract definitions hash mismatch: expected '{expectedHash}', calculated '{definitionsHash}'.");
        if (!expected.TryGetProperty("totalEvents", out var totalElement)
            || !totalElement.TryGetInt32(out var totalEvents) || totalEvents <= 0)
            throw new InvalidOperationException("goldenFixture.expected.totalEvents must be positive.");
        if (!expected.TryGetProperty("eventRowsByModule", out var rows)
            || rows.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("goldenFixture.expected.eventRowsByModule is required.");
        string[] requiredModules =
            ["technicalIndicators", "candlePatterns", "volumeAnomaly", "marketRegime", "fibonacci", "volumeProfile", "confluence"];
        var values = new List<int>();
        foreach (var module in requiredModules)
        {
            if (!rows.TryGetProperty(module, out var count) || !count.TryGetInt32(out var value) || value < 0)
                throw new InvalidOperationException($"goldenFixture expected count for '{module}' is missing or invalid.");
            values.Add(value);
        }
        if (rows.EnumerateObject().Count() != requiredModules.Length || values.Sum() != totalEvents)
            throw new InvalidOperationException("goldenFixture event module counts must exactly sum to totalEvents.");
    }

    private static void AppendCanonical(StringBuilder output, JsonElement element, bool isRoot = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject()
                    .Where(x => !(isRoot && x.NameEquals("goldenFixture")))
                    .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) output.Append(',');
                    firstProperty = false;
                    AppendPythonString(output, property.Name);
                    output.Append(':');
                    AppendCanonical(output, property.Value);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) output.Append(',');
                    firstItem = false;
                    AppendCanonical(output, item);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                AppendPythonString(output, element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                // JsonDocument rejects NaN/Infinity. Preserve the JSON numeric token itself;
                // Python's compact canonical serializer follows the same finite-number rule.
                output.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind {element.ValueKind} in technical module contract.");
        }
    }

    // Python's json.dumps defaults to ensure_ascii=True and lower-case hex escapes.
    // The contract hash is shared with Python, so Utf8JsonWriter's different escaping
    // policy cannot be used even though both encodings are valid RFC-8259 JSON.
    private static void AppendPythonString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (ch < 0x20 || ch > 0x7f)
                        output.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        output.Append(ch);
                    break;
            }
        }
        output.Append('"');
    }

    private static string HexHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Test-only fallback for direct service construction outside dependency injection.</summary>
internal sealed class BuiltInTechnicalModuleContractProvider : ITechnicalModuleContractProvider
{
    private static readonly IReadOnlyDictionary<string, string> Versions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["technicalIndicators"] = TechnicalReplayLayerService.IndicatorsVersion,
            ["candlePatterns"] = TechnicalReplayLayerService.CandlePatternsVersion,
            ["volumeAnomaly"] = TechnicalReplayLayerService.VolumeAnomalyVersion,
            ["marketRegime"] = TechnicalReplayLayerService.MarketRegimeVersion,
            ["fibonacci"] = TechnicalReplayLayerService.FibonacciVersion,
            ["volumeProfile"] = TechnicalReplayLayerService.VolumeProfileVersion,
            ["confluence"] = TechnicalReplayLayerService.ConfluenceVersion
        };

    public string ContractVersion => "btc-causal-technical-modules/v1-test-fallback";
    public string Sha256 => new('0', 64);
    public string RawFileSha256 => new('0', 64);
    public string CanonicalJson => "{}";
    public string GetCalculationVersion(string moduleKey) => Versions[moduleKey];
}
