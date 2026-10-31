using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Rebuilds the technical artifact tables the equity lane reads (TechnicalIndicators,
/// CandlePatterns) for MBB/BID from the provider-agnostic <see cref="InstrumentCandle"/>
/// store — never from Binance <see cref="Kline"/> rows. CandleVolumeStats, PatternSequences
/// and WindowVectors are no longer built for equities (EQTRIM-1: only BTC-lane code reads
/// them); their rows for the symbol/timeframe are still deleted, so old ones get cleaned up.
///
/// Contract (MBB-03):
/// - Manual/explicit invocation only. MBB is a known-but-disabled instrument in
///   <see cref="InstrumentCatalog"/>; this service is an internal lane and deliberately
///   does not consult <see cref="ProductionSymbolPolicy"/> enabled-traffic gates. It
///   still fails closed on unknown symbols and on timeframes outside the instrument's
///   declared contract (MBB: 1h + 1d; "4h" is rejected).
/// - Full rebuild semantics: all existing artifact rows for the same symbol/timeframe
///   are deleted first, then rewritten. Provider history rewrites (Revision bumps on
///   InstrumentCandles) are handled by re-running the whole symbol/timeframe — no
///   partial invalidation in v1. Re-running is idempotent.
/// - Session gaps are NOT data gaps: indicator segmentation uses a
///   <see cref="BarAdjacency"/> built from <see cref="VietnamMarketCalendar"/> expected
///   opens, so lunch/overnight/weekend/holiday breaks keep indicator state contiguous
///   while a genuinely missing expected bar still restarts it. Candle patterns operate on
///   stored-bar order, are inherently cadence-free and reuse the existing math unchanged.
/// </summary>
public sealed class MbbTechnicalRecomputeService
{
    /// <summary>
    /// The /api/mbb alias symbol. Since EQX the lane serves every equity carrying the
    /// TechnicalAnalysis + Ingestion capabilities (MBB, BID); context-only instruments
    /// (VCB, CTG, VNINDEX) and Binance instruments are rejected.
    /// </summary>
    public const string SupportedSymbol = "MBB";

    private const int MinIndicatorBars = 50;

    private readonly AppDbContext _db;
    private readonly ICandlePatternIndexer _patternIndexer;
    private readonly TechnicalIndicatorIndexer _technicalIndexer;
    private readonly VietnamMarketCalendar _calendar;
    private readonly InstrumentCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MbbTechnicalRecomputeService> _logger;

    public MbbTechnicalRecomputeService(
        AppDbContext db,
        ICandlePatternIndexer patternIndexer,
        TechnicalIndicatorIndexer technicalIndexer,
        VietnamMarketCalendar calendar,
        InstrumentCatalog? catalog = null,
        TimeProvider? timeProvider = null,
        ILogger<MbbTechnicalRecomputeService>? logger = null)
    {
        _db = db;
        _patternIndexer = patternIndexer;
        _technicalIndexer = technicalIndexer;
        _calendar = calendar;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MbbTechnicalRecomputeService>.Instance;
    }

    /// <summary>
    /// Full rebuild of technical artifacts for <paramref name="symbol"/>/
    /// <paramref name="timeframe"/> from InstrumentCandles. Throws
    /// <see cref="ArgumentException"/> for unknown symbols, symbols not stored in
    /// InstrumentCandles (BTCUSDT stays on the Klines lane), or unsupported timeframes.
    /// </summary>
    public async Task<MbbTechnicalRecomputeReport> RecomputeAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException(
                $"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'. Known instruments: " +
                string.Join(", ", _catalog.Instruments.Select(x => x.Symbol)),
                nameof(symbol));

        if (!EquityScope.IsEquityLaneAsset(instrument) || !instrument.Capabilities.Ingestion)
            throw new ArgumentException(
                $"Symbol '{instrument.Symbol}' does not use the InstrumentCandles store. " +
                "Binance instruments recompute via Klines indexers.",
                nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.TechnicalAnalysis))
            throw new ArgumentException(
                $"Symbol '{instrument.Symbol}' does not support capability 'technicalAnalysis' " +
                "(context-only instrument).",
                nameof(symbol));

        var tf = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!instrument.ActiveTimeframes.Contains(tf, StringComparer.Ordinal))
            throw new ArgumentException(
                $"timeframe '{tf}' is inactive for instrument '{instrument.Symbol}'. " +
                $"Active timeframes: {string.Join(", ", instrument.ActiveTimeframes)}.",
                nameof(timeframe));

        var generatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        var bars = await LoadBarsAsync(instrument.Symbol, tf, cancellationToken);
        if (bars.Count == 0)
        {
            _logger.LogWarning("No InstrumentCandles for {Symbol} {Timeframe}; nothing to recompute",
                instrument.Symbol, tf);
            return new MbbTechnicalRecomputeReport(
                instrument.Symbol, tf, 0, MbbRecomputeStatuses.NoData, generatedAt,
                ModuleNames().Select(m => new MbbRecomputeModuleResult(
                    m, MbbRecomputeModuleStatuses.Skipped, 0,
                    "no InstrumentCandles rows for this symbol/timeframe")).ToArray());
        }

        // Full-rebuild strategy: wipe the artifact rows of THIS symbol/timeframe only, including
        // the three tables no longer rebuilt (clears pre-EQTRIM-1 rows). Other symbols (BTCUSDT)
        // and other timeframes are never touched.
        await DeleteArtifactsAsync(instrument.Symbol, tf, cancellationToken);

        var adjacency = BuildSessionAdjacency(_calendar, tf, bars);
        var modules = new List<MbbRecomputeModuleResult>();

        // Technical indicators — needs the session adjacency and >= 50 bars.
        if (bars.Count < MinIndicatorBars)
        {
            modules.Add(new MbbRecomputeModuleResult(
                "TechnicalIndicators", MbbRecomputeModuleStatuses.Skipped, 0,
                $"requires at least {MinIndicatorBars} candles; have {bars.Count}"));
        }
        else
        {
            modules.Add(await RunModuleAsync(
                "TechnicalIndicators",
                () => _technicalIndexer.IndexAsync(instrument.Symbol, tf, bars, adjacency, cancellationToken)));
        }

        modules.Add(await RunModuleAsync(
            "CandlePatterns",
            () => _patternIndexer.IndexAsync(instrument.Symbol, tf, bars, cancellationToken)));

        var status = modules.Any(m => m.Status == MbbRecomputeModuleStatuses.Failed)
            ? MbbRecomputeStatuses.Partial
            : MbbRecomputeStatuses.Succeeded;

        _logger.LogInformation(
            "MBB recompute {Symbol} {Timeframe}: {Bars} candles -> {Modules}",
            instrument.Symbol, tf, bars.Count,
            string.Join(", ", modules.Select(m => $"{m.Module}={m.Status}:{m.RowsWritten}")));

        return new MbbTechnicalRecomputeReport(
            instrument.Symbol, tf, bars.Count, status, generatedAt, modules);
    }

    /// <summary>
    /// Adjacency for session-based instruments: bar pair (prev,next) is contiguous iff
    /// next is the immediate next EXPECTED open after prev per the Vietnam calendar.
    /// Lunch/overnight/weekend/holiday spans are contiguous; a missing expected open or
    /// an anomalous bar (e.g. the 2024-06-20 12:00 lunch bar) breaks the segment.
    /// </summary>
    internal static BarAdjacency BuildSessionAdjacency(
        VietnamMarketCalendar calendar, string timeframe, IReadOnlyList<IOhlcvBar> bars)
    {
        var firstLocal = DateOnly.FromDateTime(
            VietnamMarketCalendar.ToLocal(bars[0].OpenTimeMs).DateTime);
        var lastLocal = DateOnly.FromDateTime(
            VietnamMarketCalendar.ToLocal(bars[^1].OpenTimeMs).DateTime);

        var expected = calendar.EnumerateExpectedOpensUtcMs(timeframe, firstLocal, lastLocal);
        var nextExpected = new Dictionary<long, long>(expected.Count);
        for (var i = 0; i + 1 < expected.Count; i++)
            nextExpected[expected[i]] = expected[i + 1];

        return (prev, next) =>
            nextExpected.TryGetValue(prev, out var expectedNext) && expectedNext == next;
    }

    private async Task<MbbRecomputeModuleResult> RunModuleAsync(
        string module, Func<Task<int>> run)
    {
        try
        {
            var written = await run();
            return new MbbRecomputeModuleResult(
                module, MbbRecomputeModuleStatuses.Recomputed, written);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MBB recompute module {Module} failed", module);
            return new MbbRecomputeModuleResult(
                module, MbbRecomputeModuleStatuses.Failed, 0,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ModuleNames() => ["TechnicalIndicators", "CandlePatterns"];

    private async Task<IReadOnlyList<OhlcvBar>> LoadBarsAsync(
        string symbol, string timeframe, CancellationToken cancellationToken)
    {
        return await _db.InstrumentCandles
            .AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
            .OrderBy(c => c.OpenTimeMs)
            .Select(c => new OhlcvBar(
                c.OpenTimeMs, c.CloseTimeMs, c.Open, c.High, c.Low, c.Close, c.Volume))
            .ToListAsync(cancellationToken);
    }

    private async Task DeleteArtifactsAsync(
        string symbol, string timeframe, CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            await _db.TechnicalIndicators
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.CandlePatterns
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.CandleVolumeStats
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.PatternSequences
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.WindowVectors
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            // EF InMemory (tests) has no ExecuteDelete — delete client-side.
            _db.TechnicalIndicators.RemoveRange(await _db.TechnicalIndicators
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken));
            _db.CandlePatterns.RemoveRange(await _db.CandlePatterns
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken));
            _db.CandleVolumeStats.RemoveRange(await _db.CandleVolumeStats
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken));
            _db.PatternSequences.RemoveRange(await _db.PatternSequences
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken));
            _db.WindowVectors.RemoveRange(await _db.WindowVectors
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken));
            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();
        }
    }
}
