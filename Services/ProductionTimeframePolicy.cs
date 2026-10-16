using Backend.Options;
using Backend.Services.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Nguồn duy nhất cho các khung nến production. Dữ liệu lịch sử ở khung khác vẫn đọc được,
/// nhưng mọi pipeline ghi/backfill/rebuild phải kiểm tra policy này.
/// </summary>
public sealed class ProductionTimeframePolicy
{
    public static readonly IReadOnlyList<string> Defaults = ["1h", "4h", "1d"];

    private readonly HashSet<string> _activeSet;
    private readonly InstrumentCatalog _catalog;

    public ProductionTimeframePolicy(
        IOptions<ProductionTimeframeOptions>? options = null,
        InstrumentCatalog? catalog = null)
    {
        _catalog = catalog ?? InstrumentCatalog.Default;
        var configured = options?.Value.Active ?? Defaults;
        var active = configured
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (active.Length == 0 || active.Any(x => Timeframes.IntervalToMs(x) <= 0))
            throw new InvalidOperationException("ProductionTimeframes:Active must contain valid Binance intervals.");
        if (!new HashSet<string>(active, StringComparer.OrdinalIgnoreCase).SetEquals(Defaults))
            throw new InvalidOperationException("ProductionTimeframes:Active must contain exactly: 1h, 4h, 1d.");

        Active = active;
        _activeSet = new HashSet<string>(active, StringComparer.OrdinalIgnoreCase);

        var configuredDefault = Canonicalize(options?.Value.Default);
        Default = string.IsNullOrWhiteSpace(configuredDefault) ? "4h" : configuredDefault;
        if (!string.Equals(Default, "4h", StringComparison.Ordinal))
            throw new InvalidOperationException("ProductionTimeframes:Default must be exactly 4h.");
    }

    public IReadOnlyList<string> Active { get; }

    public string Default { get; }

    public static string Canonicalize(string? timeframe) =>
        timeframe?.Trim().ToLowerInvariant() ?? string.Empty;

    public bool IsActive(string? timeframe) => _activeSet.Contains(Canonicalize(timeframe));

    public string InactiveMessage(string timeframe) =>
        $"timeframe '{timeframe}' is inactive for production mutations. Active timeframes: {string.Join(", ", Active)}.";

    public string EnsureActive(string timeframe)
    {
        var canonical = Canonicalize(timeframe);
        if (!IsActive(canonical))
            throw new ArgumentException(InactiveMessage(timeframe), nameof(timeframe));
        return canonical;
    }

    public IReadOnlyList<string> NormalizeAndEnsureActive(IEnumerable<string> timeframes)
    {
        var normalized = timeframes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("At least one timeframe is required.", nameof(timeframes));
        return normalized.Select(EnsureActive).ToArray();
    }

    // --- Symbol-aware contract (MBB-01) -------------------------------------------------
    // The single-argument members above keep the BTC default scope for existing callers.
    // The overloads below validate the declared timeframe contract of each known
    // instrument (BTCUSDT: 1h/4h/1d; MBB: 1h/1d). They are catalog checks only: a
    // known-but-disabled instrument such as MBB can have a valid declared timeframe while
    // ProductionSymbolPolicy still rejects it for production traffic. Unknown symbols
    // fail closed.

    /// <summary>Declared active timeframes for a known instrument (catalog check, not an enabled-traffic gate).</summary>
    public IReadOnlyList<string> ActiveFor(string? symbol) => RequireInstrument(symbol).ActiveTimeframes;

    /// <summary>Declared default timeframe for a known instrument (MBB: 1d, BTCUSDT: 4h; catalog check, not an enabled-traffic gate).</summary>
    public string DefaultFor(string? symbol) => RequireInstrument(symbol).DefaultTimeframe;

    /// <summary>Checks the declared timeframe contract for a known instrument; use <see cref="ProductionSymbolPolicy"/> for enabled-traffic gating.</summary>
    public bool IsActive(string? symbol, string? timeframe)
    {
        var instrument = _catalog.Get(symbol);
        return instrument is not null
            && instrument.ActiveTimeframes.Contains(Canonicalize(timeframe), StringComparer.Ordinal);
    }

    /// <summary>Ensures the timeframe is declared for the instrument; does not by itself enable a disabled symbol.</summary>
    public string EnsureActive(string? symbol, string timeframe)
    {
        var instrument = RequireInstrument(symbol);
        var canonical = Canonicalize(timeframe);
        if (!instrument.ActiveTimeframes.Contains(canonical, StringComparer.Ordinal))
            throw new ArgumentException(InactiveMessage(symbol, timeframe), nameof(timeframe));
        return canonical;
    }

    public string InactiveMessage(string? symbol, string timeframe)
    {
        var instrument = _catalog.Get(symbol);
        var label = instrument?.Symbol ?? InstrumentCatalog.Canonicalize(symbol);
        var active = instrument?.ActiveTimeframes ?? (IReadOnlyList<string>)[];
        return $"timeframe '{Canonicalize(timeframe)}' is inactive for instrument '{label}'. Active timeframes: {string.Join(", ", active)}.";
    }

    public IReadOnlyList<string> NormalizeAndEnsureActive(string? symbol, IEnumerable<string> timeframes)
    {
        _ = RequireInstrument(symbol);
        var normalized = timeframes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("At least one timeframe is required.", nameof(timeframes));
        return normalized.Select(t => EnsureActive(symbol, t)).ToArray();
    }

    private InstrumentDescriptor RequireInstrument(string? symbol) =>
        _catalog.Get(symbol)
            ?? throw new ArgumentException(
                $"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'. Known instruments: {string.Join(", ", _catalog.Instruments.Select(x => x.Symbol))}.",
                nameof(symbol));
}
