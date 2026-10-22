using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// MBB (HOSE) candle ingestion into <see cref="InstrumentCandle"/> — the provider-agnostic
/// equity candle store. VCI is the sole write source; KBS is only used by
/// <see cref="MbbDataQualityService"/> for cross-checks, so the two adjusted-price bases
/// are never silently mixed.
///
/// Semantics differ from the Binance lane by design:
/// - Expected opens come from <see cref="VietnamMarketCalendar"/> (HOSE sessions,
///   weekend/holiday exclusion, lunch break), not from a fixed interval cadence.
/// - Missing expected opens are recomputed each run against stored bars, so a crashed or
///   partial backfill resumes idempotently with no separate gap ledger.
/// - Bars are upserted only once finalized (session close + publish grace); VCI may still
///   serve a forming latest bar, which is counted and skipped.
/// - A provider history rewrite (adjusted prices change) updates the stored row and bumps
///   <see cref="InstrumentCandle.Revision"/>; a pristine re-run writes nothing.
/// </summary>
public sealed class MbbIngestionService
{
    private readonly AppDbContext _db;
    private readonly IVciOhlcvClient _vci;
    private readonly VietnamMarketCalendar _calendar;
    private readonly MbbIngestionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MbbIngestionService> _logger;
    private readonly InstrumentDescriptor _instrument;
    private readonly InstrumentCatalog _catalog;

    public MbbIngestionService(
        AppDbContext db,
        IVciOhlcvClient vci,
        MbbIngestionOptions? options = null,
        VietnamMarketCalendar? calendar = null,
        TimeProvider? timeProvider = null,
        ILogger<MbbIngestionService>? logger = null,
        InstrumentCatalog? catalog = null)
    {
        _db = db;
        _vci = vci;
        _options = options ?? new MbbIngestionOptions();
        _calendar = calendar ?? VietnamMarketCalendar.CreateDefault(
            _options.NonTradingDates,
            TimeSpan.FromMinutes(_options.PublishGraceMinutes));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MbbIngestionService>.Instance;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _instrument = _catalog.Get(_options.Symbol)
            ?? throw new ArgumentException(
                $"Symbol '{_options.Symbol}' is not a known instrument in the catalog.", nameof(options));
    }

    public InstrumentDescriptor Instrument => _instrument;

    /// <summary>
    /// Same lane bound to another catalog symbol (EQX). The symbol must carry the
    /// Ingestion capability; options are copied per symbol (daily backfill start from
    /// <see cref="MbbIngestionOptions.DailyBackfillStartBySymbol"/>), timeframes stay
    /// configured ∩ the instrument's ActiveTimeframes. MBB yields this same instance.
    /// </summary>
    public MbbIngestionService ForSymbol(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Ingestion))
            throw new ArgumentException(
                $"Symbol '{instrument.Symbol}' does not support the equity ingestion lane.", nameof(symbol));
        if (string.Equals(instrument.Symbol, _instrument.Symbol, StringComparison.Ordinal))
            return this;
        return new MbbIngestionService(_db, _vci, _options.ForSymbol(instrument.Symbol), _calendar,
            _timeProvider, _logger, _catalog);
    }

    /// <summary>Effective timeframes: configured set ∩ the instrument's declared active set.</summary>
    internal IReadOnlyList<string> EffectiveTimeframes() =>
        _options.Timeframes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Where(t => _instrument.ActiveTimeframes.Contains(t, StringComparer.Ordinal))
            .ToArray();

    /// <summary>
    /// One sync cycle per timeframe: fetch latest bars, then fill missing expected opens
    /// bounded by <see cref="MbbIngestionOptions.MaxRequestsPerCycle"/> and
    /// <see cref="MbbIngestionOptions.MaxGapRangesPerCycle"/>.
    /// </summary>
    public async Task<MbbSyncReport> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return new MbbSyncReport(_instrument.Symbol, _timeProvider.GetUtcNow().UtcDateTime,
                MbbSyncStatuses.Disabled, []);

        var results = new List<MbbTimeframeSync>();
        var requestsLeft = Math.Max(1, _options.MaxRequestsPerCycle);
        var failures = 0;
        var timeframes = EffectiveTimeframes();

        for (var index = 0; index < timeframes.Count; index++)
        {
            var tf = timeframes[index];
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                // Reserve one request for each remaining timeframe's latest fetch so an
                // older timeframe with permanent/unfillable gaps cannot starve the rest.
                var reservedForRemaining = Math.Max(0, timeframes.Count - index - 1);
                var requestBudget = requestsLeft <= reservedForRemaining
                    ? requestsLeft
                    : requestsLeft - reservedForRemaining;
                var result = await SyncTimeframeAsync(tf, requestBudget, cancellationToken);
                requestsLeft = Math.Max(0, requestsLeft - result.FetchedRequests);
                results.Add(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning(ex, "MBB sync failed for {Symbol} {Timeframe}", _instrument.Symbol, tf);
                results.Add(new MbbTimeframeSync(tf, 0, 0, 0, 0, 0, 0, 0, 0, 0, ex.Message));
            }
        }

        // A timeframe can also report an Error without throwing (revision refetch incomplete).
        failures = Math.Max(failures, results.Count(r => r.Error is not null));
        var status = failures == 0 ? MbbSyncStatuses.Succeeded
            : results.All(r => r.Error is not null) ? MbbSyncStatuses.Failed
            : MbbSyncStatuses.Partial;
        return new MbbSyncReport(_instrument.Symbol, _timeProvider.GetUtcNow().UtcDateTime, status, results);
    }

    private async Task<MbbTimeframeSync> SyncTimeframeAsync(
        string timeframe, int requestBudget, CancellationToken cancellationToken)
    {
        var nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var fetched = 0;
        var inserted = 0;
        var updated = 0;
        var skippedForming = 0;
        var rejected = 0;
        var requestsUsed = 0;

        var revisionRequests = 0;
        string? revisionStatus = null;
        // Never store bars older than the declared history start (or older rows already
        // stored by an explicit earlier backfill): e.g. VNINDEX's malformed 2005-era rows.
        var floorMs = await HistoryFloorMsAsync(timeframe, cancellationToken);

        // 1. Latest bars. A changed PRICE on an already-stored finalized bar means the
        // provider re-based its adjusted history (ex-date): VCI/KBS rewrite the WHOLE series,
        // so patching only the recent window would leave two adjustment factors in the DB.
        // Revisions therefore trigger a full-history refetch that is written in one
        // SaveChanges, or not at all (stored rows then stay consistently on the previous
        // basis and the next cycle detects the revision again).
        if (requestBudget > 0)
        {
            var latest = await _vci.GetBarsAsync(_instrument.Symbol, timeframe, nowSec,
                Math.Max(1, _options.LatestCountBack), cancellationToken);
            requestsUsed++;
            fetched += latest.Count;

            var revised = await CountPriceRevisionsAsync(timeframe, latest, cancellationToken);
            IReadOnlyList<ProviderOhlcvBar> toWrite = latest;
            if (revised > 0)
            {
                var refetch = await FetchFullHistoryAsync(timeframe, latest, cancellationToken);
                revisionRequests = refetch.Requests;
                fetched += refetch.FetchedBars;
                if (!refetch.Complete)
                {
                    _logger.LogWarning(
                        "{Symbol} {Timeframe}: {Revised} stored bars changed price but the full-history refetch " +
                        "did not complete within {MaxPages} pages; nothing written to avoid mixing adjustment bases",
                        _instrument.Symbol, timeframe, revised, _options.RevisionRefetchMaxPages);
                    return new MbbTimeframeSync(timeframe, fetched, 0, 0, 0, 0, 0, 0, 0,
                        requestsUsed + revisionRequests,
                        $"revision-refetch-incomplete: {revised} stored bars changed price; full history not covered " +
                        $"within {_options.RevisionRefetchMaxPages} pages - nothing written",
                        revisionRequests, RevisionRefetchStatuses.Incomplete);
                }
                _logger.LogInformation(
                    "{Symbol} {Timeframe}: {Revised} stored bars changed price (provider re-adjustment); " +
                    "rewriting full history ({Bars} bars, {Requests} requests)",
                    _instrument.Symbol, timeframe, revised, refetch.Bars.Count, refetch.Requests);
                toWrite = refetch.Bars;
                revisionStatus = RevisionRefetchStatuses.Completed;
            }

            var write = await UpsertBarsAsync(timeframe, toWrite, cancellationToken, floorMs);
            inserted += write.Inserted;
            updated += write.Updated;
            skippedForming += write.SkippedForming;
            rejected += write.RejectedInvalid;
        }

        // 2. Recompute missing expected opens; fill only ranges inside the configured
        // gap-fill window, newest first. Older gaps remain visible in MissingExpectedBars
        // but do not consume the request budget every cycle.
        var expected = ExpectedOpens(timeframe);
        var missing = await FindMissingExpectedOpensAsync(timeframe, expected, cancellationToken);
        var ranges = VietnamMarketCalendar.GroupMissingRanges(expected, missing);
        var gapFillFloorMs = GapFillStartUtcMs();
        var fillableRanges = ranges
            .Where(r => r.EndOpenTimeMs >= gapFillFloorMs)
            .OrderByDescending(r => r.EndOpenTimeMs)
            .ToArray();
        var processed = 0;
        foreach (var range in fillableRanges)
        {
            if (processed >= _options.MaxGapRangesPerCycle || requestsUsed >= requestBudget)
                break;
            var batch = await FetchRangeAsync(timeframe, range, cancellationToken);
            requestsUsed++;
            fetched += batch.Count;
            var write = await UpsertBarsAsync(timeframe, batch, cancellationToken, floorMs);
            inserted += write.Inserted;
            updated += write.Updated;
            skippedForming += write.SkippedForming;
            rejected += write.RejectedInvalid;
            processed++;
        }

        var remaining = Math.Max(0, fillableRanges.Length - processed);
        return new MbbTimeframeSync(timeframe, fetched, inserted, updated, skippedForming,
            rejected, missing.Count, processed, remaining, requestsUsed + revisionRequests,
            null, revisionRequests, revisionStatus);
    }

    /// <summary>
    /// Finalized, valid provider bars whose stored row exists with a different OHLC price.
    /// Volume-only changes are not a re-basing signal and are patched in place.
    /// </summary>
    internal async Task<int> CountPriceRevisionsAsync(
        string timeframe, IReadOnlyList<ProviderOhlcvBar> bars, CancellationToken cancellationToken)
    {
        if (bars.Count == 0) return 0;
        var nowUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var openTimes = bars.Select(b => b.OpenTimeMs).ToHashSet();
        var stored = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == _instrument.Symbol
                && c.Timeframe == timeframe
                && openTimes.Contains(c.OpenTimeMs))
            .Select(c => new { c.OpenTimeMs, c.Open, c.High, c.Low, c.Close })
            .ToDictionaryAsync(c => c.OpenTimeMs, cancellationToken);
        var revised = 0;
        foreach (var bar in bars)
        {
            if (!_calendar.IsFinalized(timeframe, bar.OpenTimeMs, nowUtcMs) || !IsValidOhlcv(bar)) continue;
            if (stored.TryGetValue(bar.OpenTimeMs, out var row)
                && (row.Open != bar.Open || row.High != bar.High || row.Low != bar.Low || row.Close != bar.Close))
                revised++;
        }
        return revised;
    }

    /// <summary>Oldest open the lane keeps: min(declared history start, earliest stored row).</summary>
    private async Task<long> HistoryFloorMsAsync(string timeframe, CancellationToken cancellationToken)
    {
        var earliestStored = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == _instrument.Symbol && c.Timeframe == timeframe)
            .MinAsync(c => (long?)c.OpenTimeMs, cancellationToken);
        var startLocal = BackfillStartLocal(timeframe);
        var startOpenMs = timeframe == EquityCandleTimeframes.Day
            ? VietnamMarketCalendar.DailyOpenUtcMs(startLocal)
            : VietnamMarketCalendar.HourlyOpensUtcMs(startLocal)[0];
        return Math.Min(earliestStored ?? startOpenMs, startOpenMs);
    }

    internal sealed record FullHistoryFetch(
        IReadOnlyList<ProviderOhlcvBar> Bars, int Requests, int FetchedBars, bool Complete);

    /// <summary>
    /// Pages backward from the latest batch until the earliest stored bar (or the backfill
    /// start, whichever is older) is covered or the provider stops yielding, bounded by
    /// <see cref="MbbIngestionOptions.RevisionRefetchMaxPages"/>. Pure fetch: writes nothing.
    /// </summary>
    private async Task<FullHistoryFetch> FetchFullHistoryAsync(
        string timeframe, IReadOnlyList<ProviderOhlcvBar> latest, CancellationToken cancellationToken,
        long? extraFloorMs = null)
    {
        if (latest.Count == 0) return new FullHistoryFetch([], 0, 0, false);
        var floorMs = await HistoryFloorMsAsync(timeframe, cancellationToken);
        if (extraFloorMs is { } extra) floorMs = Math.Min(floorMs, extra);

        var byOpen = new SortedDictionary<long, ProviderOhlcvBar>();
        foreach (var bar in latest) byOpen[bar.OpenTimeMs] = bar;
        var requests = 0;
        var fetchedBars = 0;
        var pageSize = Math.Max(1, _options.BatchCountBack);
        var maxPages = Math.Max(1, _options.RevisionRefetchMaxPages);
        var cursorSec = (byOpen.First().Key / 1000) - 1;
        while (byOpen.First().Key > floorMs)
        {
            if (requests >= maxPages)
                return new FullHistoryFetch([], requests, fetchedBars, false);
            if (requests > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, _options.RequestDelayMs)), cancellationToken);
            var page = await _vci.GetBarsAsync(_instrument.Symbol, timeframe, cursorSec, pageSize, cancellationToken);
            requests++;
            fetchedBars += page.Count;
            if (page.Count == 0) break; // provider history exhausted - nothing older exists
            foreach (var bar in page) byOpen.TryAdd(bar.OpenTimeMs, bar);
            if (page.Count < pageSize) break;
            cursorSec = (page.Min(b => b.OpenTimeMs) / 1000) - 1;
        }
        return new FullHistoryFetch(byOpen.Values.ToArray(), requests, fetchedBars, true);
    }

    /// <summary>
    /// Full-history bounded backfill: pages backward with VCI countBack until
    /// <paramref name="fromLocal"/> is covered, the provider stops yielding, or the
    /// request budget is exhausted. Idempotent — reruns only write new/revised bars.
    /// </summary>
    public async Task<MbbTimeframeSync> BackfillAsync(
        string timeframe,
        DateOnly? fromLocal = null,
        DateOnly? toLocal = null,
        CancellationToken cancellationToken = default)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_instrument.ActiveTimeframes.Contains(timeframe, StringComparer.Ordinal))
            throw new ArgumentException(
                $"timeframe '{timeframe}' is inactive for instrument '{_instrument.Symbol}'.",
                nameof(timeframe));
        if (!_options.Enabled)
            return new MbbTimeframeSync(timeframe, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                $"{_instrument.Symbol} ingestion is disabled.");

        var from = fromLocal ?? BackfillStartLocal(timeframe);
        var nowUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var fromOpenMs = timeframe == EquityCandleTimeframes.Day
            ? VietnamMarketCalendar.DailyOpenUtcMs(from)
            : VietnamMarketCalendar.HourlyOpensUtcMs(from)[0];
        var toSec = toLocal.HasValue
            ? VietnamMarketCalendar.ToUtcMs(toLocal.Value, VietnamMarketCalendar.AfternoonCloseLocal) / 1000
            : nowUtcMs / 1000;

        // Pages are buffered, never written one by one: a page on a NEW adjustment basis written
        // next to stored rows on the OLD basis would mix two factors permanently (the next sync's
        // latest window would already match and never detect it).
        var fetched = 0;
        var requests = Math.Max(1, _options.MaxRequestsPerCycle);
        var cursorSec = toSec;
        var pages = new SortedDictionary<long, ProviderOhlcvBar>();
        while (requests > 0 && !cancellationToken.IsCancellationRequested)
        {
            var batch = await _vci.GetBarsAsync(_instrument.Symbol, timeframe, cursorSec,
                Math.Max(1, _options.BatchCountBack), cancellationToken);
            requests--;
            if (batch.Count == 0) break;
            fetched += batch.Count;
            foreach (var bar in batch) pages.TryAdd(bar.OpenTimeMs, bar);

            var earliest = batch[0].OpenTimeMs;
            if (earliest <= fromOpenMs || batch.Count < _options.BatchCountBack) break;
            cursorSec = (earliest / 1000) - 1;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, _options.RequestDelayMs)), cancellationToken);
        }
        var used = _options.MaxRequestsPerCycle - requests;

        // Revision detection over the fetched pages plus, when the range ends in the past, a probe
        // of the latest bars (a disjoint older range cannot reveal a re-basing on its own).
        IReadOnlyList<ProviderOhlcvBar> latest = toLocal.HasValue
            ? await ProbeLatestAsync(timeframe, nowUtcMs, cancellationToken)
            : pages.Values.TakeLast(Math.Max(1, _options.LatestCountBack)).ToArray();
        if (toLocal.HasValue)
        {
            used++;
            fetched += latest.Count;
        }
        var revised = await CountPriceRevisionsAsync(timeframe,
            pages.Values.Concat(latest).DistinctBy(b => b.OpenTimeMs).ToArray(), cancellationToken);

        IReadOnlyList<ProviderOhlcvBar> toWrite;
        long writeFloorMs;
        int revisionRequests = 0;
        string? revisionStatus = null;
        if (revised > 0)
        {
            // Re-based history: refetch the WHOLE series from now and write it in one save (or nothing).
            var refetch = await FetchFullHistoryAsync(timeframe, latest, cancellationToken, extraFloorMs: fromOpenMs);
            revisionRequests = refetch.Requests;
            fetched += refetch.FetchedBars;
            if (!refetch.Complete)
            {
                _logger.LogWarning(
                    "{Symbol} {Timeframe} backfill: {Revised} stored bars changed price but the full-history refetch " +
                    "did not complete within {MaxPages} pages; nothing written", _instrument.Symbol, timeframe, revised,
                    _options.RevisionRefetchMaxPages);
                return new MbbTimeframeSync(timeframe, fetched, 0, 0, 0, 0, 0, 0, 0, used + revisionRequests,
                    $"revision-refetch-incomplete: {revised} stored bars changed price; full history not covered " +
                    $"within {_options.RevisionRefetchMaxPages} pages - nothing written",
                    revisionRequests, RevisionRefetchStatuses.Incomplete);
            }
            toWrite = refetch.Bars;
            writeFloorMs = Math.Min(fromOpenMs, await HistoryFloorMsAsync(timeframe, cancellationToken));
            revisionStatus = RevisionRefetchStatuses.Completed;
        }
        else
        {
            toWrite = pages.Values.ToArray();
            // Pages overshoot `from`; bars before it are not part of the requested history.
            writeFloorMs = fromOpenMs;
        }

        var write = await UpsertBarsAsync(timeframe, toWrite, cancellationToken, writeFloorMs);
        var missing = await FindMissingExpectedOpensAsync(timeframe, ExpectedOpens(timeframe), cancellationToken);
        return new MbbTimeframeSync(timeframe, fetched, write.Inserted, write.Updated, write.SkippedForming,
            write.RejectedInvalid, missing.Count, 0, 0, used + revisionRequests, null, revisionRequests, revisionStatus);
    }

    private Task<IReadOnlyList<ProviderOhlcvBar>> ProbeLatestAsync(string timeframe, long nowUtcMs, CancellationToken ct) =>
        _vci.GetBarsAsync(_instrument.Symbol, timeframe, nowUtcMs / 1000, Math.Max(1, _options.LatestCountBack), ct);

    /// <summary>
    /// Expected opens over the full instrument history window [backfill start, today],
    /// filtered to finalized bars only — a still-forming latest bar is not "missing".
    /// </summary>
    internal IReadOnlyList<long> ExpectedOpens(string timeframe)
    {
        var nowUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var todayLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(nowUtcMs).DateTime);
        return _calendar
            .EnumerateExpectedOpensUtcMs(timeframe, BackfillStartLocal(timeframe), todayLocal)
            .Where(o => _calendar.IsFinalized(timeframe, o, nowUtcMs))
            .ToArray();
    }

    internal DateOnly BackfillStartLocal(string timeframe)
    {
        var raw = timeframe == EquityCandleTimeframes.Day
            ? _options.DailyBackfillStartLocal
            : _options.HourlyBackfillStartLocal;
        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", out var date))
            throw new InvalidOperationException(
                $"MbbIngestion backfill start '{raw}' is not a yyyy-MM-dd local date.");
        return date;
    }

    private long GapFillStartUtcMs()
    {
        if (!DateOnly.TryParseExact(_options.GapFillStartLocal, "yyyy-MM-dd", out var date))
            throw new InvalidOperationException(
                $"MbbIngestion gap-fill start '{_options.GapFillStartLocal}' is not a yyyy-MM-dd local date.");
        return VietnamMarketCalendar.ToUtcMs(date, TimeOnly.MinValue);
    }

    /// <summary>Expected opens that have no stored row — recomputed fresh every call.</summary>
    internal async Task<IReadOnlySet<long>> FindMissingExpectedOpensAsync(
        string timeframe, IReadOnlyList<long> expectedOpens, CancellationToken cancellationToken)
    {
        var stored = await _db.InstrumentCandles
            .Where(c => c.Symbol == _instrument.Symbol && c.Timeframe == timeframe)
            .Select(c => c.OpenTimeMs)
            .ToListAsync(cancellationToken);
        var storedSet = stored.ToHashSet();
        return expectedOpens.Where(o => !storedSet.Contains(o)).ToHashSet();
    }

    /// <summary>
    /// Fetch one gap range from VCI: `to` just past the range end and `countBack` sized to
    /// the missing run plus context. Oversized ranges fill partially; the remainder is
    /// recomputed next cycle — that recompute is the resume mechanism.
    /// </summary>
    private Task<IReadOnlyList<ProviderOhlcvBar>> FetchRangeAsync(
        string timeframe, CandleGapRange range, CancellationToken cancellationToken)
    {
        var toSec = (_calendar.SessionCloseUtcMs(timeframe, range.EndOpenTimeMs) / 1000) + 3600;
        var countBack = (int)Math.Clamp(range.MissingBars + 100, 100, _options.BatchCountBack);
        return _vci.GetBarsAsync(_instrument.Symbol, timeframe, toSec, countBack, cancellationToken);
    }

    /// <summary>
    /// Upsert provider bars into InstrumentCandles. Only finalized bars are written;
    /// identical re-fetches are no-ops; changed values update in place and bump Revision.
    /// Every row carries VCI/adjusted/HOSE/Asia-Ho_Chi_Minh provenance from the catalog.
    /// </summary>
    internal async Task<UpsertResult> UpsertBarsAsync(
        string timeframe, IReadOnlyList<ProviderOhlcvBar> bars, CancellationToken cancellationToken = default,
        long? minOpenMs = null)
    {
        var result = new UpsertResult();
        if (minOpenMs.HasValue && bars.Any(b => b.OpenTimeMs < minOpenMs.Value))
        {
            result.SkippedBeforeStart = bars.Count(b => b.OpenTimeMs < minOpenMs.Value);
            bars = bars.Where(b => b.OpenTimeMs >= minOpenMs.Value).ToArray();
        }
        if (bars.Count == 0) return result;

        var nowUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var openTimes = bars.Select(b => b.OpenTimeMs).ToHashSet();
        var existing = await _db.InstrumentCandles
            .Where(c => c.Symbol == _instrument.Symbol
                && c.Timeframe == timeframe
                && openTimes.Contains(c.OpenTimeMs))
            .ToDictionaryAsync(c => c.OpenTimeMs, cancellationToken);

        foreach (var bar in bars)
        {
            if (!_calendar.IsFinalized(timeframe, bar.OpenTimeMs, nowUtcMs))
            {
                result.SkippedForming++;
                continue;
            }
            if (!IsValidOhlcv(bar))
            {
                result.RejectedInvalid++;
                _logger.LogWarning(
                    "Rejected invalid {Symbol} {Timeframe} bar at {OpenMs}: O={Open} H={High} L={Low} C={Close} V={Volume}",
                    _instrument.Symbol, timeframe, bar.OpenTimeMs,
                    bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
                continue;
            }

            var closeMs = _calendar.SessionCloseUtcMs(timeframe, bar.OpenTimeMs);
            if (existing.TryGetValue(bar.OpenTimeMs, out var row))
            {
                if (row.Open == bar.Open && row.High == bar.High && row.Low == bar.Low
                    && row.Close == bar.Close && row.Volume == bar.Volume
                    && row.CloseTimeMs == closeMs)
                    continue; // pristine re-fetch — idempotent no-op

                row.Open = bar.Open;
                row.High = bar.High;
                row.Low = bar.Low;
                row.Close = bar.Close;
                row.Volume = bar.Volume;
                row.CloseTimeMs = closeMs;
                row.UpdatedAtUtc = nowUtc;
                row.Revision++;
                result.Updated++;
            }
            else
            {
                _db.InstrumentCandles.Add(new InstrumentCandle
                {
                    Symbol = _instrument.Symbol,
                    Timeframe = timeframe,
                    OpenTimeMs = bar.OpenTimeMs,
                    CloseTimeMs = closeMs,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume,
                    Provider = _instrument.DataProvider,
                    PriceBasis = _instrument.PriceBasis,
                    Venue = _instrument.Venue,
                    SourceTimezone = _instrument.Timezone,
                    FetchedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                    Revision = 0,
                });
                result.Inserted++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>OHLCV sanity: positive prices, non-negative volume, high/low envelope.</summary>
    internal static bool IsValidOhlcv(ProviderOhlcvBar bar) =>
        bar.Open > 0 && bar.High > 0 && bar.Low > 0 && bar.Close > 0
        && bar.Volume >= 0
        && bar.High >= bar.Low
        && bar.High >= bar.Open && bar.High >= bar.Close
        && bar.Low <= bar.Open && bar.Low <= bar.Close;

    internal sealed class UpsertResult
    {
        public int Inserted;
        public int Updated;
        public int SkippedForming;
        public int RejectedInvalid;
        public int SkippedBeforeStart;
    }
}
