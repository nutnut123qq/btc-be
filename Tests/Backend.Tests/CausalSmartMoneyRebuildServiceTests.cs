using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public sealed class CausalSmartMoneyRebuildServiceTests
{
    private const long Hour = 3_600_000;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(100 * Hour);

    [Fact]
    public async Task Dry_run_is_read_only_and_reports_gap_provenance_and_invalid_duration()
    {
        await using var db = Context();
        var invalid = Bar(2, 102, 107, 97, 103);
        invalid.CloseTimeMs = 2 * Hour + 123;
        db.Klines.AddRange(
            Bar(0, 100, 105, 95, 101),
            Bar(1, 101, 106, 96, 102),
            invalid,
            Bar(4, 103, 108, 98, 104));
        db.KlineGapStates.Add(new KlineGapState
        {
            Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = 3 * Hour,
            EndOpenTimeMs = 3 * Hour, MissingBars = 1, Status = KlineGapStatuses.Unavailable
        });
        await db.SaveChangesAsync();

        var result = await Create(db).RebuildAsync(new CausalSmartMoneyRebuildRequest
            { DryRun = true, Timeframe = "1h", MaxCandles = 10 });

        Assert.Equal(4, result.CandidateCandles);
        Assert.Equal(1, result.InvalidDurationCandles);
        Assert.Contains(result.GapBoundaries, x => x.BoundaryType == "invalid_duration");
        Assert.Contains(result.GapBoundaries, x => x.BoundaryType == "missing_candles"
            && x.LedgerStatus == KlineGapStatuses.Unavailable);
        Assert.Empty(await db.CausalSmartMoneyEvents.ToListAsync());
        Assert.Empty(await db.CausalSmartMoneyRebuildCheckpoints.ToListAsync());
        Assert.Empty(await db.SmartMoneyStructures.ToListAsync());
    }

    [Fact]
    public async Task Apply_resumes_and_updates_fvg_lifecycle_without_changing_decision_hash()
    {
        await using var db = Context();
        db.Klines.AddRange(
            Bar(0, 95, 100, 90, 98),
            Bar(1, 101, 105, 99, 104),
            Bar(2, 111, 115, 110, 114),
            Bar(3, 108, 112, 99, 101));
        await db.SaveChangesAsync();
        var service = Create(db);

        var first = await service.RebuildAsync(new CausalSmartMoneyRebuildRequest
            { DryRun = false, Timeframe = "1h", MaxCandles = 3 });
        var active = await db.CausalSmartMoneyEvents.SingleAsync(x => x.EventType == "FVG_BULL");
        var immutableHash = active.DecisionEvidenceSha256;
        var decisionCutoff = active.AvailableTimeMs;
        Assert.Equal("active", active.State);
        Assert.Null(active.MitigatedAtMs);

        var second = await service.RebuildAsync(new CausalSmartMoneyRebuildRequest
            { DryRun = false, Timeframe = "1h", MaxCandles = 3 });
        db.ChangeTracker.Clear();
        var mitigated = await db.CausalSmartMoneyEvents.SingleAsync(x => x.EventType == "FVG_BULL");

        Assert.Equal(3, first.CandidateCandles);
        Assert.Equal(1, second.CandidateCandles);
        Assert.Equal("mitigated", mitigated.State);
        Assert.True(mitigated.MitigatedAtMs > decisionCutoff);
        Assert.Equal(3 * Hour, mitigated.MitigationSourceOpenTimeMs);
        Assert.Equal(immutableHash, mitigated.DecisionEvidenceSha256);
        using var decision = JsonDocument.Parse(mitigated.DecisionEvidenceJson);
        Assert.Equal("active", decision.RootElement.GetProperty("stateAtAsOf").GetString());
        Assert.Equal(JsonValueKind.Null, decision.RootElement.GetProperty("mitigatedAtMs").ValueKind);
        Assert.False(mitigated.MitigatedAtMs <= decisionCutoff); // historical cutoff remains active
        Assert.Empty(await db.SmartMoneyStructures.ToListAsync());
    }

    [Fact]
    public async Task Retry_after_checkpoint_loss_is_idempotent()
    {
        await using var db = Context();
        db.Klines.AddRange(
            Bar(0, 95, 100, 90, 98),
            Bar(1, 101, 105, 99, 104),
            Bar(2, 111, 115, 110, 114));
        await db.SaveChangesAsync();
        var service = Create(db);
        var request = new CausalSmartMoneyRebuildRequest { DryRun = false, Timeframe = "1h", MaxCandles = 3 };

        await service.RebuildAsync(request);
        var checkpoint = await db.CausalSmartMoneyRebuildCheckpoints.SingleAsync();
        checkpoint.LastProcessedOpenTimeMs = null;
        checkpoint.ProcessedCandleCount = 0;
        await db.SaveChangesAsync();
        var retry = await service.RebuildAsync(request);

        Assert.Equal(0, retry.InsertedEvents);
        Assert.True(retry.ExistingEvents > 0);
        Assert.Equal(1, await db.CausalSmartMoneyEvents.CountAsync(x => x.EventType == "FVG_BULL"));
    }

    [Fact]
    public async Task Apply_fails_closed_when_immutable_core_drifts_without_version_bump()
    {
        await using var db = Context();
        db.Klines.AddRange(
            Bar(0, 95, 100, 90, 98),
            Bar(1, 101, 105, 99, 104),
            Bar(2, 111, 115, 110, 114));
        await db.SaveChangesAsync();
        var service = Create(db);
        var request = new CausalSmartMoneyRebuildRequest { DryRun = false, Timeframe = "1h", MaxCandles = 3 };
        await service.RebuildAsync(request);
        var row = await db.CausalSmartMoneyEvents.SingleAsync(x => x.EventType == "FVG_BULL");
        row.DecisionEvidenceSha256 = new string('0', 64);
        var checkpoint = await db.CausalSmartMoneyRebuildCheckpoints.SingleAsync();
        checkpoint.LastProcessedOpenTimeMs = null;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<CausalSmartMoneyRebuildLimitException>(() => service.RebuildAsync(request));

        Assert.Equal("SMC_IMMUTABLE_CORE_DRIFT", error.Code);
        db.ChangeTracker.Clear();
        Assert.Equal("failed", (await db.CausalSmartMoneyRebuildCheckpoints.SingleAsync()).Status);
    }

    [Fact]
    public async Task Exact_multiple_batch_is_marked_complete_without_empty_follow_up()
    {
        await using var db = Context();
        db.Klines.AddRange(Bar(0, 100, 101, 99, 100), Bar(1, 100, 101, 99, 100));
        await db.SaveChangesAsync();

        var result = await Create(db).RebuildAsync(new CausalSmartMoneyRebuildRequest
            { DryRun = false, Timeframe = "1h", MaxCandles = 2 });

        Assert.Equal("complete", result.Status);
        Assert.Equal("complete", (await db.CausalSmartMoneyRebuildCheckpoints.SingleAsync()).Status);
    }

    [Fact]
    public async Task Gap_resets_pivot_trend_and_fvg_state()
    {
        await using var db = Context();
        var beforeGap = new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11)
        };
        var afterGap = new[]
        {
            Bar(7, 14, 20, 13, 19), Bar(8, 19, 21, 18, 20), Bar(9, 20, 22, 19, 21)
        };
        db.Klines.AddRange(beforeGap.Concat(afterGap));
        await db.SaveChangesAsync();

        await Create(db).RebuildAsync(new CausalSmartMoneyRebuildRequest
            { DryRun = false, Timeframe = "1h", MaxCandles = 20 });
        var persisted = await db.CausalSmartMoneyEvents.AsNoTracking().ToArrayAsync();

        Assert.DoesNotContain(persisted, x => x.OriginTimeMs >= 7 * Hour
            && x.EventType is "BOS_BULL" or "CHOCH_BULL" or "BOS_BEAR" or "CHOCH_BEAR");
        Assert.All(persisted.Where(x => x.OriginTimeMs >= 7 * Hour), x => Assert.Equal(7 * Hour, x.SegmentStartOpenTimeMs));
    }

    [Fact]
    public async Task No_event_or_invalid_row_still_advances_checkpoint()
    {
        await using var db = Context();
        var invalid = Bar(0, 100, 101, 99, 100);
        invalid.CloseTimeMs = 123;
        db.Klines.Add(invalid);
        await db.SaveChangesAsync();

        var result = await Create(db).RebuildAsync(new CausalSmartMoneyRebuildRequest
            { DryRun = false, Timeframe = "1h", MaxCandles = 1 });

        Assert.Equal(0, result.EstimatedEvents);
        Assert.Equal(0, result.LastProcessedOpenTimeMs);
        Assert.Equal(0, (await db.CausalSmartMoneyRebuildCheckpoints.SingleAsync()).LastProcessedOpenTimeMs);
    }

    [Fact]
    public async Task Hard_batch_cap_fails_before_database_mutation()
    {
        await using var db = Context();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Create(db).RebuildAsync(
            new CausalSmartMoneyRebuildRequest
            {
                DryRun = false, Timeframe = "1h",
                MaxCandles = CausalSmartMoneyRebuildService.MaximumBatchCandles + 1
            }));
        Assert.Empty(await db.CausalSmartMoneyRebuildCheckpoints.ToListAsync());
    }

    [Fact]
    public async Task Preview_from_beginning_cannot_be_used_for_apply()
    {
        await using var db = Context();
        await Assert.ThrowsAsync<ArgumentException>(() => Create(db).RebuildAsync(
            new CausalSmartMoneyRebuildRequest
            {
                DryRun = false, PreviewFromBeginning = true, Timeframe = "1h", MaxCandles = 1
            }));
        Assert.Empty(await db.CausalSmartMoneyRebuildCheckpoints.ToListAsync());
    }

    private static CausalSmartMoneyRebuildService Create(AppDbContext db) => new(
        db,
        new ProductionSymbolPolicy(),
        new ProductionTimeframePolicy(),
        new FixedTimeProvider(Now));

    private static AppDbContext Context()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Kline Bar(int index, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "BTCUSDT", Timeframe = "1h",
        OpenTimeMs = index * Hour,
        CloseTimeMs = (index + 1) * Hour - 1,
        Open = open, High = high, Low = low, Close = close, Volume = 10
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
