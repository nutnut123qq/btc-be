using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Single source of truth for the enabled production market scope. The shared
/// <see cref="InstrumentCatalog"/> declares every known instrument (BTCUSDT, MBB);
/// this policy still gates which symbols existing pipelines may serve or mutate —
/// a registered instrument is only active once its data lanes are live.
/// </summary>
public sealed class ProductionSymbolPolicy
{
    public const string Symbol = "BTCUSDT";

    private readonly InstrumentCatalog _catalog;

    public ProductionSymbolPolicy(InstrumentCatalog? catalog = null)
    {
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    public static string Canonicalize(string? symbol) => InstrumentCatalog.Canonicalize(symbol);

    /// <summary>Known instrument in the shared catalog (includes not-yet-enabled symbols).</summary>
    public bool IsKnown(string? symbol) => _catalog.IsKnown(symbol);

    public InstrumentDescriptor? GetInstrument(string? symbol) => _catalog.Get(symbol);

    /// <summary>Enabled for production traffic right now. MBB stays inactive until its lanes land.</summary>
    public bool IsActive(string? symbol) =>
        _catalog.Get(symbol)?.Enabled == true;

    public string InactiveMessage(string? symbol)
    {
        var canonical = Canonicalize(symbol);
        if (_catalog.Get(canonical) is { Enabled: false })
            return $"symbol '{canonical}' is a known instrument but is not enabled in the production scope. Enabled instruments: {string.Join(", ", EnabledSymbols())}.";
        return $"symbol '{canonical}' is inactive. The production scope is {Symbol} only.";
    }

    public string EnsureActive(string? symbol)
    {
        var canonical = Canonicalize(symbol);
        if (!IsActive(canonical))
            throw new ArgumentException(InactiveMessage(symbol), nameof(symbol));
        return canonical;
    }

    public string NormalizeListAndEnsureActive(string symbols)
    {
        var normalized = symbols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EnsureActive)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("At least one symbol is required.", nameof(symbols));
        return string.Join(',', normalized);
    }

    private IEnumerable<string> EnabledSymbols() =>
        _catalog.Instruments.Where(x => x.Enabled).Select(x => x.Symbol);
}
