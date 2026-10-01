using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.Json;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Backend.Services;

public interface IKlineDataQualityService
{
    Task<KlineDataIssuesResponse> GetIssuesAsync(string symbol, string timeframe, long? startOpenTimeMs,
        long? endOpenTimeMs, int limit, CancellationToken cancellationToken = default);
    Task<KlineDataRepairResponse> RepairAsync(KlineDataRepairRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class KlineDataQualityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class KlineDataRepairSourceException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class KlineDataQualityService : IKlineDataQualityService
{
    internal const int MaxRepairBars = 1_000;
    internal const string RepairSource = "binance-spot-rest-api-v3-klines";
    internal const string RepairSourceEndpoint = "https://api.binance.com/api/v3/klines";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ApplyGates = new(StringComparer.Ordinal);
    private readonly AppDbContext _db;
    private readonly IBinanceKlinesService _binance;
    private readonly DataAuditCache _auditCache;
    private readonly TimeProvider _timeProvider;
    private readonly ProductionSymbolPolicy _symbolPolicy;
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public KlineDataQualityService(
        AppDbContext db,
        IBinanceKlinesService binance,
        DataAuditCache auditCache,
        TimeProvider timeProvider,
        ProductionSymbolPolicy symbolPolicy,
        ProductionTimeframePolicy timeframePolicy)
    {
        _db = db;
        _binance = binance;
        _auditCache = auditCache;
        _timeProvider = timeProvider;
        _symbolPolicy = symbolPolicy;
        _timeframePolicy = timeframePolicy;
    }

    public async Task<KlineDataIssuesResponse> GetIssuesAsync(
        string symbol,
        string timeframe,
        long? startOpenTimeMs,
        long? endOpenTimeMs,
        int limit,
        CancellationToken cancellationToken = default)
    {
        (symbol, timeframe, var intervalMs) = ValidateScope(symbol, timeframe);
        limit = Math.Clamp(limit, 1, MaxRepairBars);
        var now = _timeProvider.GetUtcNow();
        var auditEnd = DataAuditService.CalculateAuditEndOpenTimeMs(now.ToUnixTimeMilliseconds(), intervalMs);
        var rangeStart = startOpenTimeMs ?? long.MinValue;
        var rangeEnd = Math.Min(endOpenTimeMs ?? auditEnd, auditEnd);
        if (rangeStart > rangeEnd)
            throw new KlineDataQualityException("INVALID_RANGE", "startOpenTimeMs must not exceed the last finalized requested end.");

        var gapsQuery = _db.KlineGapStates.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.Status != KlineGapStatuses.Filled
                && x.StartOpenTimeMs <= rangeEnd && x.EndOpenTimeMs >= rangeStart);
        var invalidQuery = _db.Klines.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.OpenTimeMs >= rangeStart && x.OpenTimeMs <= rangeEnd
                && x.CloseTimeMs <= now.ToUnixTimeMilliseconds()
                && x.CloseTimeMs - x.OpenTimeMs + 1 != intervalMs);

        // One scoped DbContext does not support concurrent operations; keep these bounded queries sequential.
        var ledger = await gapsQuery.OrderBy(x => x.StartOpenTimeMs).ToListAsync(cancellationToken);
        var invalidCount = await invalidQuery.LongCountAsync(cancellationToken);
        var invalid = await invalidQuery.OrderBy(x => x.OpenTimeMs).Take(limit + 1).ToListAsync(cancellationToken);
        var openTimesQuery = _db.Klines.AsNoTracking().Where(x => x.Symbol == symbol && x.Timeframe == timeframe
            && x.OpenTimeMs <= rangeEnd && x.CloseTimeMs <= now.ToUnixTimeMilliseconds());
        if (startOpenTimeMs.HasValue) openTimesQuery = openTimesQuery.Where(x => x.OpenTimeMs >= rangeStart);
        var openTimes = await openTimesQuery.Select(x => x.OpenTimeMs).Distinct().OrderBy(x => x)
            .ToListAsync(cancellationToken);
        var physicalGaps = DiscoverPhysicalGaps(openTimes, startOpenTimeMs, rangeEnd, intervalMs);
        var repairs = await _db.KlineDataRepairRuns.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .OrderByDescending(x => x.AppliedAtUtc).ThenByDescending(x => x.Id)
            .Take(20).ToListAsync(cancellationToken);

        var issues = new List<KlineDataIssueDto>();
        issues.AddRange(physicalGaps.Select(x => MapPhysicalGap(x,
            ledger.Where(state => state.StartOpenTimeMs <= x.EndOpenTimeMs && state.EndOpenTimeMs >= x.StartOpenTimeMs)
                .OrderBy(state => state.EndOpenTimeMs - state.StartOpenTimeMs).FirstOrDefault(),
            symbol, timeframe, intervalMs)));
        issues.AddRange(invalid.Select(x => MapInvalidDuration(x, intervalMs)));
        var ordered = issues.OrderBy(x => x.StartOpenTimeMs).ThenBy(x => x.IssueType, StringComparer.Ordinal)
            .Take(limit).ToArray();
        var total = physicalGaps.Count + invalidCount;
        return new KlineDataIssuesResponse(
            KlineDataQualityTaxonomy.Version, symbol, timeframe, now.UtcDateTime, auditEnd, total,
            total > ordered.LongLength, ordered, repairs.Select(MapRepair).ToArray(),
            [
                "Only finalized BTCUSDT 1h/4h/1d candles are audited.",
                "Without startOpenTimeMs, missing-interval discovery begins at the earliest stored finalized candle; pass an aligned start to audit head coverage explicitly.",
                "Missing OHLCV is never interpolated. Repair requires an exact Binance Spot kline for each open time.",
                "A source-verified candle repair invalidates downstream derived artifacts; rebuild them through their versioned bounded rebuild workflows."
            ]);
    }

    public async Task<KlineDataRepairResponse> RepairAsync(
        KlineDataRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.DryRun)
            return await RepairCoreAsync(request, cancellationToken);
        var key = $"{ProductionSymbolPolicy.Canonicalize(request.Symbol)}\u001f{ProductionTimeframePolicy.Canonicalize(request.Timeframe)}";
        var gate = ApplyGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await RepairCoreAsync(request, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<KlineDataRepairResponse> RepairCoreAsync(
        KlineDataRepairRequest request,
        CancellationToken cancellationToken)
    {
        (var symbol, var timeframe, var intervalMs) = ValidateScope(request.Symbol, request.Timeframe);
        if (!KlineDataQualityTaxonomy.IssueTypes.All.Contains(request.IssueType))
            throw new KlineDataQualityException("INVALID_ISSUE_TYPE", "issueType must be missing_interval or invalid_duration.");
        ValidateRepairRange(request.StartOpenTimeMs, request.EndOpenTimeMs, intervalMs);
        var requestedBarsLong = ((request.EndOpenTimeMs - request.StartOpenTimeMs) / intervalMs) + 1;
        if (requestedBarsLong > MaxRepairBars)
            throw new KlineDataQualityException("REPAIR_LIMIT_EXCEEDED", $"A repair is limited to {MaxRepairBars} candles.");
        var requestedBars = checked((int)requestedBarsLong);
        var now = _timeProvider.GetUtcNow();
        var auditEnd = DataAuditService.CalculateAuditEndOpenTimeMs(now.ToUnixTimeMilliseconds(), intervalMs);
        if (request.EndOpenTimeMs > auditEnd)
            throw new KlineDataQualityException("FORMING_RANGE_NOT_REPAIRABLE", "Repair range must end at or before the last finalized candle open time.");

        IReadOnlyList<KlineDto> sourceRows;
        try
        {
            sourceRows = await _binance.GetKlinesForVerificationAsync(symbol, timeframe, requestedBars,
                request.StartOpenTimeMs, request.EndOpenTimeMs, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new KlineDataRepairSourceException("Binance Spot source verification failed; no data was changed.", ex);
        }

        var sourceCheckedAt = _timeProvider.GetUtcNow();
        var expectedOpenTimes = Enumerable.Range(0, requestedBars)
            .Select(i => checked(request.StartOpenTimeMs + i * intervalMs)).ToArray();
        var expectedSet = expectedOpenTimes.ToHashSet();
        var verified = sourceRows
            .Where(x => expectedSet.Contains(x.OpenTimeMs)
                && x.CloseTimeMs <= sourceCheckedAt.ToUnixTimeMilliseconds()
                && HasExpectedDuration(x.OpenTimeMs, x.CloseTimeMs, intervalMs)
                && IsValidOhlcv(x))
            .GroupBy(x => x.OpenTimeMs).Select(x => x.Last())
            .OrderBy(x => x.OpenTimeMs).ToArray();
        var sourceClassification = sourceRows.Count == 0
            ? KlineDataQualityTaxonomy.SourceClassifications.BinanceSpotEmpty
            : verified.Length == 0
                ? KlineDataQualityTaxonomy.SourceClassifications.BinanceSpotRejected
                : KlineDataQualityTaxonomy.SourceClassifications.BinanceSpotVerified;
        var sourceEvidenceJson = SerializeEvidence(verified);
        var sourceEvidenceSha = Sha256(sourceEvidenceJson);

        if (request.DryRun)
        {
            var previewRows = await LoadExistingAsync(symbol, timeframe, request.StartOpenTimeMs,
                request.EndOpenTimeMs, tracking: false, cancellationToken);
            var preview = BuildPlan(request, symbol, timeframe, intervalMs, expectedOpenTimes, verified,
                sourceEvidenceSha, previewRows);
            return BuildResponse(request, symbol, timeframe, requestedBars, sourceRows.Count, verified.Length,
                preview.Actions, sourceClassification, sourceCheckedAt.UtcDateTime, sourceEvidenceSha,
                preview.PlanSha256, null, false, false);
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedPlanSha256) || request.ExpectedPlanSha256.Length != 64)
            throw new KlineDataQualityException("PREVIEW_SHA_REQUIRED", "Apply requires the exact planSha256 returned by a preceding dry run.");
        if (verified.Length == 0)
            throw new KlineDataQualityException("SOURCE_NOT_VERIFIED", "Apply requires at least one exact finalized Binance Spot source candle; no data was changed.");

        var expectedPlanSha = request.ExpectedPlanSha256.ToLowerInvariant();
        await using IDbContextTransaction? transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        if (transaction is not null)
        {
            var lockKey = $"kline-data-repair:{symbol}:{timeframe}";
            // Every PostgreSQL INSERT/UPDATE on Klines takes ROW EXCLUSIVE. This bounded table
            // lock conflicts with it, so ingestion and a repair cannot change the before-state
            // between the preview comparison and commit, even in another process.
            await _db.Database.ExecuteSqlRawAsync(
                "LOCK TABLE \"Klines\" IN SHARE ROW EXCLUSIVE MODE;", cancellationToken);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", cancellationToken);
        }

        var prior = await _db.KlineDataRepairRuns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanSha256 == expectedPlanSha, cancellationToken);
        if (prior is not null)
        {
            if (prior.Symbol != symbol || prior.Timeframe != timeframe || prior.IssueType != request.IssueType
                || prior.StartOpenTimeMs != request.StartOpenTimeMs || prior.EndOpenTimeMs != request.EndOpenTimeMs)
                throw new KlineDataQualityException("PREVIEW_DRIFT", "The preview belongs to a different repair scope.");
            if (!string.Equals(prior.SourceEvidenceSha256, sourceEvidenceSha, StringComparison.OrdinalIgnoreCase))
                throw new KlineDataQualityException("SOURCE_PREVIEW_DRIFT", "Binance source differs from the previously applied preview; run a new dry run.");
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return BuildAlreadyAppliedResponse(request, prior, sourceRows.Count, sourceCheckedAt.UtcDateTime);
        }

        // The before-state, action set and preview comparison are intentionally inside the
        // serializable/advisory-locked transaction. Ingestion and other processes cannot
        // change the same symbol/timeframe between comparison and writes.
        var existing = await LoadExistingAsync(symbol, timeframe, request.StartOpenTimeMs,
            request.EndOpenTimeMs, tracking: true, cancellationToken);
        var plan = BuildPlan(request, symbol, timeframe, intervalMs, expectedOpenTimes, verified,
            sourceEvidenceSha, existing);
        if (!FixedTimeEquals(expectedPlanSha, plan.PlanSha256))
            throw new KlineDataQualityException("PREVIEW_DRIFT", "Database state or Binance source differs from the preview; run a new dry run.");

        var actions = plan.Actions;
        foreach (var item in actions.Insert)
            _db.Klines.Add(ToEntity(symbol, timeframe, item));
        foreach (var item in actions.Replace)
            Copy(item.Target, item.Source);
        await _db.SaveChangesAsync(cancellationToken);
        await ReconcileOverlappingGapLedgerAsync(symbol, timeframe, request.StartOpenTimeMs,
            request.EndOpenTimeMs, intervalMs, sourceCheckedAt.UtcDateTime, cancellationToken);

        var receipt = new KlineDataRepairRun
        {
            PlanSha256 = plan.PlanSha256,
            SourceEvidenceSha256 = sourceEvidenceSha,
            Symbol = symbol,
            Timeframe = timeframe,
            IssueType = request.IssueType,
            StartOpenTimeMs = request.StartOpenTimeMs,
            EndOpenTimeMs = request.EndOpenTimeMs,
            RequestedBars = requestedBars,
            VerifiedSourceBars = verified.Length,
            InsertedBars = actions.Insert.Count,
            ReplacedBars = actions.Replace.Count,
            NoopBars = actions.NoopOpenTimes.Count,
            UnresolvedBars = actions.UnresolvedOpenTimes.Count,
            UnresolvedOpenTimeMsJson = JsonSerializer.Serialize(actions.UnresolvedOpenTimes, JsonOptions),
            SourceClassification = sourceClassification,
            SourceEvidenceJson = sourceEvidenceJson,
            BeforeEvidenceJson = plan.BeforeEvidenceJson,
            SourceCheckedAtUtc = sourceCheckedAt.UtcDateTime,
            AppliedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        };
        _db.KlineDataRepairRuns.Add(receipt);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        _auditCache.Invalidate(symbol);
        return BuildResponse(request, symbol, timeframe, requestedBars, sourceRows.Count, verified.Length,
            actions, sourceClassification, sourceCheckedAt.UtcDateTime, sourceEvidenceSha, plan.PlanSha256, receipt.Id, true, false);
    }

    private Task<List<Kline>> LoadExistingAsync(string symbol, string timeframe, long start, long end,
        bool tracking, CancellationToken cancellationToken)
    {
        var query = _db.Klines.Where(x => x.Symbol == symbol && x.Timeframe == timeframe
            && x.OpenTimeMs >= start && x.OpenTimeMs <= end);
        if (!tracking) query = query.AsNoTracking();
        return query.OrderBy(x => x.OpenTimeMs).ToListAsync(cancellationToken);
    }

    private static RepairPlan BuildPlan(KlineDataRepairRequest request, string symbol, string timeframe,
        long intervalMs, IReadOnlyList<long> expectedOpenTimes, IReadOnlyList<KlineDto> verified,
        string sourceEvidenceSha, IReadOnlyList<Kline> existing)
    {
        var beforeEvidenceJson = SerializeEvidence(existing.Select(ToDto));
        var actions = BuildActions(request.IssueType, expectedOpenTimes, verified, existing, intervalMs);
        var planSha = Sha256(JsonSerializer.Serialize(new
        {
            taxonomyVersion = KlineDataQualityTaxonomy.Version,
            symbol,
            timeframe,
            request.IssueType,
            request.StartOpenTimeMs,
            request.EndOpenTimeMs,
            sourceEvidenceSha,
            beforeEvidenceSha256 = Sha256(beforeEvidenceJson),
            insert = actions.Insert.Select(x => x.OpenTimeMs),
            replace = actions.Replace.Select(x => x.Source.OpenTimeMs),
            noop = actions.NoopOpenTimes,
            unresolved = actions.UnresolvedOpenTimes
        }, JsonOptions));
        return new RepairPlan(actions, beforeEvidenceJson, planSha);
    }

    private (string Symbol, string Timeframe, long IntervalMs) ValidateScope(string symbol, string timeframe)
    {
        symbol = ProductionSymbolPolicy.Canonicalize(symbol);
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_symbolPolicy.IsActive(symbol))
            throw new KlineDataQualityException("UNSUPPORTED_SYMBOL", _symbolPolicy.InactiveMessage(symbol));
        if (!_timeframePolicy.IsActive(timeframe))
            throw new KlineDataQualityException("INACTIVE_TIMEFRAME", _timeframePolicy.InactiveMessage(timeframe));
        return (symbol, timeframe, Timeframes.IntervalToMs(timeframe));
    }

    private static void ValidateRepairRange(long start, long end, long intervalMs)
    {
        if (start < 0 || end < start || start % intervalMs != 0 || end % intervalMs != 0
            || (end - start) % intervalMs != 0)
            throw new KlineDataQualityException("INVALID_RANGE", "Repair bounds must be non-negative, aligned candle open times on one active timeframe.");
    }

    private static RepairActions BuildActions(string issueType, IReadOnlyList<long> expectedOpenTimes,
        IReadOnlyList<KlineDto> verified, IReadOnlyList<Kline> existing, long intervalMs)
    {
        var source = verified.ToDictionary(x => x.OpenTimeMs);
        var stored = existing.ToDictionary(x => x.OpenTimeMs);
        var insert = new List<KlineDto>();
        var replace = new List<Replacement>();
        var noop = new List<long>();
        var unresolved = new List<long>();
        foreach (var open in expectedOpenTimes)
        {
            if (!source.TryGetValue(open, out var fetched))
            {
                unresolved.Add(open);
                continue;
            }
            stored.TryGetValue(open, out var current);
            if (issueType == KlineDataQualityTaxonomy.IssueTypes.MissingInterval)
            {
                if (current is null) insert.Add(fetched);
                else if (HasExpectedDuration(current.OpenTimeMs, current.CloseTimeMs, intervalMs)) noop.Add(open);
                else unresolved.Add(open); // wrong issue type must never overwrite a malformed stored row
            }
            else
            {
                if (current is null) unresolved.Add(open);
                else if (HasExpectedDuration(current.OpenTimeMs, current.CloseTimeMs, intervalMs)) noop.Add(open);
                else replace.Add(new Replacement(current, fetched));
            }
        }
        return new RepairActions(insert, replace, noop, unresolved);
    }

    private async Task ReconcileOverlappingGapLedgerAsync(string symbol, string timeframe, long start, long end,
        long intervalMs, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var states = await _db.KlineGapStates.Where(x => x.Symbol == symbol && x.Timeframe == timeframe
            && x.Status != KlineGapStatuses.Filled && x.StartOpenTimeMs <= end && x.EndOpenTimeMs >= start)
            .ToListAsync(cancellationToken);
        foreach (var state in states)
        {
            var rows = await _db.Klines.AsNoTracking().Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.OpenTimeMs >= state.StartOpenTimeMs && x.OpenTimeMs <= state.EndOpenTimeMs)
                .Select(x => new { x.OpenTimeMs, x.CloseTimeMs }).ToListAsync(cancellationToken);
            var expected = ((state.EndOpenTimeMs - state.StartOpenTimeMs) / intervalMs) + 1;
            var valid = rows.LongCount(x => HasExpectedDuration(x.OpenTimeMs, x.CloseTimeMs, intervalMs));
            state.MissingBars = Math.Max(0, expected - valid);
            if (state.MissingBars != 0) continue;
            state.Status = KlineGapStatuses.Filled;
            state.NextRetryAtUtc = null;
            state.Reason = "Filled by source-verified bounded repair.";
            state.UpdatedAtUtc = nowUtc;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyList<PhysicalGap> DiscoverPhysicalGaps(
        IReadOnlyList<long> orderedOpenTimes,
        long? requestedStart,
        long rangeEnd,
        long intervalMs)
    {
        if (intervalMs <= 0 || rangeEnd < 0) return [];
        var gaps = new List<PhysicalGap>();
        if (orderedOpenTimes.Count == 0)
        {
            if (requestedStart.HasValue && requestedStart.Value <= rangeEnd)
                gaps.Add(new PhysicalGap(requestedStart.Value, rangeEnd,
                    ((rangeEnd - requestedStart.Value) / intervalMs) + 1));
            return gaps;
        }
        var expected = requestedStart ?? orderedOpenTimes[0];
        foreach (var open in orderedOpenTimes)
        {
            if (open < expected) continue;
            if (open > expected)
                gaps.Add(new PhysicalGap(expected, open - intervalMs, (open - expected) / intervalMs));
            expected = open <= long.MaxValue - intervalMs ? open + intervalMs : long.MaxValue;
        }
        if (expected <= rangeEnd)
            gaps.Add(new PhysicalGap(expected, rangeEnd, ((rangeEnd - expected) / intervalMs) + 1));
        return gaps;
    }

    private static KlineDataIssueDto MapPhysicalGap(PhysicalGap gap, KlineGapState? row,
        string symbol, string timeframe, long intervalMs)
    {
        var unavailable = row?.Status == KlineGapStatuses.Unavailable;
        var failed = row?.Reason?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true;
        var cause = unavailable ? "upstream_no_data_confirmed"
            : failed ? "upstream_transport_failure"
            : row?.AttemptCount > 0 ? "upstream_no_data_retry" : "absent_from_store";
        var state = unavailable ? KlineDataQualityTaxonomy.ResolutionStates.SourceUnavailable
            : row?.NextRetryAtUtc.HasValue == true ? KlineDataQualityTaxonomy.ResolutionStates.RetryScheduled
            : KlineDataQualityTaxonomy.ResolutionStates.Open;
        var source = unavailable || row?.AttemptCount > 0
            ? KlineDataQualityTaxonomy.SourceClassifications.BinanceSpotEmpty
            : KlineDataQualityTaxonomy.SourceClassifications.NotChecked;
        if (failed) source = KlineDataQualityTaxonomy.SourceClassifications.BinanceSpotError;
        return new KlineDataIssueDto(row is null ? $"physical-gap:{gap.StartOpenTimeMs}:{gap.EndOpenTimeMs}" : $"gap:{row.Id}",
            symbol, timeframe, KlineDataQualityTaxonomy.IssueTypes.MissingInterval, cause, state,
            gap.StartOpenTimeMs, gap.EndOpenTimeMs, gap.MissingBars, intervalMs, null,
            row?.FirstDetectedAtUtc, row?.UpdatedAtUtc, !unavailable,
            new KlineIssueEvidenceDto(row is null ? "finalized-open-time-sequence-scan-v1" : "persistent-gap-ledger-v1",
                source, RepairSource, row?.AttemptCount ?? 0, row?.LastAttemptAtUtc, row?.NextRetryAtUtc, row?.Reason),
            KlineDataQualityTaxonomy.AffectedDownstreamArtifacts);
    }

    private static KlineDataIssueDto MapInvalidDuration(Kline row, long intervalMs)
    {
        var actual = row.CloseTimeMs >= row.OpenTimeMs ? row.CloseTimeMs - row.OpenTimeMs + 1 : row.CloseTimeMs - row.OpenTimeMs;
        var cause = row.CloseTimeMs < row.OpenTimeMs ? "close_before_open" : "duration_mismatch";
        return new KlineDataIssueDto($"duration:{row.OpenTimeMs}", row.Symbol, row.Timeframe,
            KlineDataQualityTaxonomy.IssueTypes.InvalidDuration, cause,
            KlineDataQualityTaxonomy.ResolutionStates.Open, row.OpenTimeMs, row.OpenTimeMs, 1,
            intervalMs, actual, null, null, true,
            new KlineIssueEvidenceDto("stored-close-duration-audit-v1",
                KlineDataQualityTaxonomy.SourceClassifications.NotChecked, RepairSource, 0, null, null,
                "Stored closeTime-openTime+1 does not equal the timeframe interval."),
            KlineDataQualityTaxonomy.AffectedDownstreamArtifacts);
    }

    private static KlineRepairAuditDto MapRepair(KlineDataRepairRun row) => new(row.Id, row.PlanSha256,
        row.SourceEvidenceSha256, row.IssueType, row.StartOpenTimeMs, row.EndOpenTimeMs, row.RequestedBars,
        row.VerifiedSourceBars, row.InsertedBars, row.ReplacedBars, row.NoopBars, row.UnresolvedBars,
        row.SourceClassification, row.SourceCheckedAtUtc, row.AppliedAtUtc);

    private static KlineDataRepairResponse BuildResponse(KlineDataRepairRequest request, string symbol,
        string timeframe, int requestedBars, int sourceRows, int verifiedRows, RepairActions actions,
        string sourceClassification, DateTime checkedAt, string sourceSha, string planSha, long? receiptId,
        bool applied, bool alreadyApplied) => new(
            KlineDataQualityTaxonomy.Version, symbol, timeframe, request.IssueType, request.DryRun, applied,
            alreadyApplied, request.StartOpenTimeMs, request.EndOpenTimeMs, requestedBars, sourceRows, verifiedRows,
            actions.Insert.Count, actions.Replace.Count, actions.NoopOpenTimes.Count, actions.UnresolvedOpenTimes,
            sourceClassification, RepairSourceEndpoint, checkedAt, sourceSha, planSha, receiptId,
            applied && (actions.Insert.Count > 0 || actions.Replace.Count > 0),
            KlineDataQualityTaxonomy.AffectedDownstreamArtifacts,
            [
                "No OHLCV values are interpolated; only exact, finalized Binance Spot source candles are eligible.",
                "Apply is bounded to 1000 candles and requires the exact dry-run plan SHA-256.",
                "Derived artifacts are not silently rewritten by this source-data repair."
            ]);

    private static KlineDataRepairResponse BuildAlreadyAppliedResponse(KlineDataRepairRequest request,
        KlineDataRepairRun prior, int sourceRows, DateTime checkedAt)
    {
        IReadOnlyList<long> unresolved;
        try
        {
            unresolved = JsonSerializer.Deserialize<long[]>(prior.UnresolvedOpenTimeMsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            throw new KlineDataQualityException("REPAIR_RECEIPT_INVALID", "Stored repair receipt has invalid unresolved-range evidence.");
        }
        if (unresolved.Count != prior.UnresolvedBars)
            throw new KlineDataQualityException("REPAIR_RECEIPT_INVALID", "Stored repair receipt counts do not match its unresolved-range evidence.");
        return new KlineDataRepairResponse(
            KlineDataQualityTaxonomy.Version, prior.Symbol, prior.Timeframe, prior.IssueType, request.DryRun,
            true, true, prior.StartOpenTimeMs, prior.EndOpenTimeMs, prior.RequestedBars, sourceRows,
            prior.VerifiedSourceBars, prior.InsertedBars, prior.ReplacedBars, prior.NoopBars, unresolved,
            prior.SourceClassification, RepairSourceEndpoint, checkedAt, prior.SourceEvidenceSha256,
            prior.PlanSha256, prior.Id, prior.InsertedBars + prior.ReplacedBars > 0,
            KlineDataQualityTaxonomy.AffectedDownstreamArtifacts,
            [
                "This exact preview was already applied; counts are preserved from its immutable receipt.",
                "No OHLCV values were interpolated; only exact finalized Binance Spot source candles were eligible.",
                "Derived artifacts are not silently rewritten by this source-data repair."
            ]);
    }

    private static bool HasExpectedDuration(long open, long close, long intervalMs) =>
        open <= long.MaxValue - (intervalMs - 1) && close == open + intervalMs - 1;

    private static bool IsValidOhlcv(KlineDto x) => x.Open > 0 && x.High > 0 && x.Low > 0 && x.Close > 0
        && x.High >= x.Low && x.High >= Math.Max(x.Open, x.Close) && x.Low <= Math.Min(x.Open, x.Close)
        && x.Volume >= 0 && x.QuoteVolume >= 0 && x.TradeCount >= 0;

    private static string SerializeEvidence(IEnumerable<KlineDto> rows) => JsonSerializer.Serialize(rows
        .OrderBy(x => x.OpenTimeMs).Select(x => new { x.OpenTimeMs, x.CloseTimeMs, x.Open, x.High, x.Low,
            x.Close, x.Volume, x.QuoteVolume, x.TradeCount, x.TakerBuyVolume, x.TakerBuyQuoteVolume }), JsonOptions);

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedTimeEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }

    private static KlineDto ToDto(Kline x) => new() { OpenTimeMs = x.OpenTimeMs, CloseTimeMs = x.CloseTimeMs,
        Open = x.Open, High = x.High, Low = x.Low, Close = x.Close, Volume = x.Volume,
        QuoteVolume = x.QuoteVolume, TradeCount = x.TradeCount, TakerBuyVolume = x.TakerBuyVolume,
        TakerBuyQuoteVolume = x.TakerBuyQuoteVolume };
    private static Kline ToEntity(string symbol, string timeframe, KlineDto x) => new() { Symbol = symbol,
        Timeframe = timeframe, OpenTimeMs = x.OpenTimeMs, CloseTimeMs = x.CloseTimeMs, Open = x.Open,
        High = x.High, Low = x.Low, Close = x.Close, Volume = x.Volume, QuoteVolume = x.QuoteVolume,
        TradeCount = x.TradeCount, TakerBuyVolume = x.TakerBuyVolume, TakerBuyQuoteVolume = x.TakerBuyQuoteVolume };
    private static void Copy(Kline target, KlineDto source)
    {
        target.CloseTimeMs = source.CloseTimeMs; target.Open = source.Open; target.High = source.High;
        target.Low = source.Low; target.Close = source.Close; target.Volume = source.Volume;
        target.QuoteVolume = source.QuoteVolume; target.TradeCount = source.TradeCount;
        target.TakerBuyVolume = source.TakerBuyVolume; target.TakerBuyQuoteVolume = source.TakerBuyQuoteVolume;
    }

    private sealed record Replacement(Kline Target, KlineDto Source);
    private sealed record RepairActions(IReadOnlyList<KlineDto> Insert, IReadOnlyList<Replacement> Replace,
        IReadOnlyList<long> NoopOpenTimes, IReadOnlyList<long> UnresolvedOpenTimes);
    private sealed record RepairPlan(RepairActions Actions, string BeforeEvidenceJson, string PlanSha256);
    internal sealed record PhysicalGap(long StartOpenTimeMs, long EndOpenTimeMs, long MissingBars);
}
