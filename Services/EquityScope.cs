using System.Collections.Concurrent;
using Backend.Services.Models;

namespace Backend.Services;

/// <summary>Equity-lane capability a route requires (EQX spec §1/§2).</summary>
public enum EquityCapability
{
    /// <summary>Any equity/index instrument (list surfaces).</summary>
    None,
    Ingestion,
    TechnicalAnalysis,
    Decision,
}

/// <summary>Outcome of <see cref="EquityScope.Resolve"/>: an instrument or an API error.</summary>
public sealed record EquityScopeResult(
    InstrumentDescriptor? Instrument,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Ok => Instrument is not null;
}

/// <summary>
/// Symbol → descriptor resolver for the /api/equities lane. Replaces the BTC enabled-traffic
/// gate (<see cref="ProductionSymbolPolicy"/>) on routes marked
/// <c>[SkipProductionSymbolScope]</c>: every equity is Enabled=false by design, so the lane
/// checks catalog capabilities instead. Errors follow spec §2 exactly:
/// unknown → 404 UNKNOWN_SYMBOL; known but not an equity/index or missing the capability →
/// 400 UNSUPPORTED_CAPABILITY; timeframe outside ActiveTimeframes → 400 INVALID_TIMEFRAME.
/// </summary>
public static class EquityScope
{
    public const string UnknownSymbol = "UNKNOWN_SYMBOL";
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";
    public const string InvalidTimeframe = "INVALID_TIMEFRAME";
    public const string LaneBusy = "LANE_BUSY";

    /// <summary>Asset classes served by the equity lane (crypto stays on the Binance lane).</summary>
    public static bool IsEquityLaneAsset(InstrumentDescriptor instrument) =>
        instrument.AssetClass is InstrumentAssetClasses.Equity or InstrumentAssetClasses.Index;

    public static bool Has(InstrumentDescriptor instrument, EquityCapability capability) =>
        IsEquityLaneAsset(instrument) && capability switch
        {
            EquityCapability.None => true,
            EquityCapability.Ingestion => instrument.Capabilities.Ingestion,
            // TA on the equity lane is computed from InstrumentCandles, so it needs ingestion too.
            EquityCapability.TechnicalAnalysis => instrument.Capabilities.Ingestion && instrument.Capabilities.TechnicalAnalysis,
            EquityCapability.Decision => instrument.Capabilities.Ingestion && instrument.Capabilities.Decision,
            _ => false,
        };

    public static string CapabilityName(EquityCapability capability) => capability switch
    {
        EquityCapability.Ingestion => "ingestion",
        EquityCapability.TechnicalAnalysis => "technicalAnalysis",
        EquityCapability.Decision => "decision",
        _ => "equity-lane",
    };

    public static EquityScopeResult Resolve(
        InstrumentCatalog catalog, string? symbol, EquityCapability capability)
    {
        var instrument = catalog.Get(symbol);
        if (instrument is null)
        {
            return new EquityScopeResult(null, StatusCodes.Status404NotFound, UnknownSymbol,
                $"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.");
        }
        if (!IsEquityLaneAsset(instrument))
        {
            return new EquityScopeResult(null, StatusCodes.Status400BadRequest, UnsupportedCapability,
                $"Symbol '{instrument.Symbol}' ({instrument.AssetClass}) is not served by the equity lane; " +
                $"capability '{CapabilityName(capability)}' is unsupported here.");
        }
        if (!Has(instrument, capability))
        {
            return new EquityScopeResult(null, StatusCodes.Status400BadRequest, UnsupportedCapability,
                $"Symbol '{instrument.Symbol}' does not support capability '{CapabilityName(capability)}'.");
        }
        return new EquityScopeResult(instrument, StatusCodes.Status200OK, null, null);
    }

    /// <summary>Canonical timeframe if it is one of the instrument's ActiveTimeframes.</summary>
    public static bool TryResolveTimeframe(InstrumentDescriptor instrument, string? timeframe, out string resolved)
    {
        resolved = ProductionTimeframePolicy.Canonicalize(timeframe);
        return !string.IsNullOrWhiteSpace(resolved)
            && instrument.ActiveTimeframes.Contains(resolved, StringComparer.Ordinal);
    }

    public static string InvalidTimeframeMessage(InstrumentDescriptor instrument, string? timeframe) =>
        $"timeframe must be one of {string.Join(", ", instrument.ActiveTimeframes)} for {instrument.Symbol}; got '{timeframe}'.";
}

/// <summary>
/// Per-symbol lane lock shared by the admin endpoints (both /api/equities and the /api/mbb
/// alias) and <see cref="EquityIngestionWorker"/>: sync/backfill/recompute/observe of one
/// symbol never overlap. Non-blocking — a busy lane answers 409 LANE_BUSY (admin) or is
/// skipped for the tick (worker). Keys are catalog-canonical symbols only (resolved through
/// <see cref="InstrumentCatalog"/> before reaching here), so the dictionary is bounded by the
/// catalog size — no user-controlled key growth (AGENTS pitfall #20).
/// </summary>
public static class EquityLaneLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>Lease for <paramref name="symbol"/>, or null when the lane is busy.</summary>
    public static IDisposable? TryAcquire(string symbol)
    {
        if (!InstrumentCatalog.Default.IsKnown(symbol))
            throw new ArgumentException($"Lane lock requires a catalog symbol; got '{symbol}'.", nameof(symbol));
        var gate = Gates.GetOrAdd(InstrumentCatalog.Canonicalize(symbol), _ => new SemaphoreSlim(1, 1));
        return gate.Wait(0) ? new Lease(gate) : null;
    }

    public static bool IsBusy(string symbol) =>
        Gates.TryGetValue(InstrumentCatalog.Canonicalize(symbol), out var gate) && gate.CurrentCount == 0;

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
