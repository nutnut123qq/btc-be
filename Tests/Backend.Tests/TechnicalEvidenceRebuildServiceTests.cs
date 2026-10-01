using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public sealed class TechnicalEvidenceRebuildServiceTests
{
    [Fact]
    public async Task Dry_run_estimates_without_mutating_checkpoint_or_records()
    {
        await using var db = Context();
        Seed(db, 2);
        var service = Create(db, eventAtEveryClose: true);

        var result = await service.RebuildAsync(new TechnicalEvidenceRebuildRequest
        {
            DryRun = true, MaxCandles = 2, Timeframe = "1h"
        });

        Assert.Equal(2, result.CandidateCandles);
        Assert.Equal(2, result.EstimatedSparseRecords);
        Assert.True(result.EstimatedEnvelopeBytes > 0);
        Assert.Empty(await db.TechnicalEvidenceRecords.ToListAsync());
        Assert.Empty(await db.TechnicalEvidenceRebuildCheckpoints.ToListAsync());
    }

    [Fact]
    public async Task Apply_is_idempotent_for_same_contract_hash_and_calculation_version()
    {
        await using var db = Context();
        Seed(db, 2);
        var service = Create(db, eventAtEveryClose: true);
        var request = new TechnicalEvidenceRebuildRequest
            { DryRun = false, MaxCandles = 2, Timeframe = "1h" };

        var first = await service.RebuildAsync(request);
        var checkpoint = await db.TechnicalEvidenceRebuildCheckpoints.SingleAsync();
        checkpoint.LastProcessedCloseTimeMs = null; // simulate a safe retry after checkpoint loss
        await db.SaveChangesAsync();
        var second = await service.RebuildAsync(request);

        Assert.Equal(2, first.InsertedRecords);
        Assert.Equal(0, second.InsertedRecords);
        Assert.Equal(2, second.ExistingRecords);
        Assert.Equal(2, await db.TechnicalEvidenceRecords.CountAsync());
        Assert.All(await db.TechnicalEvidenceRecords.ToListAsync(), x =>
            Assert.Equal("finite-window-indicator-events-v1", x.CalculationVersion));
    }

    [Fact]
    public async Task No_event_batch_still_advances_checkpoint()
    {
        await using var db = Context();
        var closes = Seed(db, 3);
        var service = Create(db, eventAtEveryClose: false);

        var result = await service.RebuildAsync(new TechnicalEvidenceRebuildRequest
            { DryRun = false, MaxCandles = 3, Timeframe = "1h" });

        Assert.Equal(0, result.InsertedRecords);
        Assert.Equal(closes[^1], result.LastProcessedCloseTimeMs);
        Assert.Equal(closes[^1], (await db.TechnicalEvidenceRebuildCheckpoints.SingleAsync()).LastProcessedCloseTimeMs);
    }

    [Fact]
    public async Task Concurrent_apply_calls_are_serialized_and_do_not_duplicate_sparse_records()
    {
        await using var db = Context();
        Seed(db, 2);
        var service = Create(db, eventAtEveryClose: true);
        var request = new TechnicalEvidenceRebuildRequest
            { DryRun = false, MaxCandles = 2, Timeframe = "1h" };

        var results = await Task.WhenAll(service.RebuildAsync(request), service.RebuildAsync(request));

        Assert.Equal(2, results.Sum(x => x.InsertedRecords));
        Assert.Equal(2, await db.TechnicalEvidenceRecords.CountAsync());
        Assert.Single(await db.TechnicalEvidenceRebuildCheckpoints.ToListAsync());
    }

    [Fact]
    public async Task Batch_cap_fails_before_any_replay_or_database_mutation()
    {
        await using var db = Context();
        var service = Create(db, eventAtEveryClose: true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RebuildAsync(
            new TechnicalEvidenceRebuildRequest { DryRun = false, MaxCandles = 101, Timeframe = "1h" }));
        Assert.Empty(await db.TechnicalEvidenceRebuildCheckpoints.ToListAsync());
    }

    private static TechnicalEvidenceRebuildService Create(AppDbContext db, bool eventAtEveryClose)
    {
        var contract = new BuiltInTechnicalModuleContractProvider();
        return new TechnicalEvidenceRebuildService(
            db,
            new FakeReplay(eventAtEveryClose, contract),
            contract,
            new ProductionSymbolPolicy(),
            new ProductionTimeframePolicy(),
            TimeProvider.System);
    }

    private static AppDbContext Context()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static long[] Seed(AppDbContext db, int count)
    {
        const long hour = 3_600_000;
        var start = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeMilliseconds();
        var rows = Enumerable.Range(0, count).Select(i => new Kline
        {
            Symbol = "BTCUSDT", Timeframe = "1h",
            OpenTimeMs = start + i * hour,
            CloseTimeMs = start + (i + 1) * hour - 1,
            Open = 100, High = 102, Low = 99, Close = 101, Volume = 10
        }).ToArray();
        db.Klines.AddRange(rows);
        db.SaveChanges();
        return rows.Select(x => x.CloseTimeMs).ToArray();
    }

    private sealed class FakeReplay(bool eventAtEveryClose, ITechnicalModuleContractProvider contract)
        : ISmartMoneyService
    {
        public Task<List<SmartMoneyStructure>> GetSmartMoneyStructuresAsync(
            string symbol, string timeframe, int lookbackBars, CancellationToken ct = default) =>
            Task.FromResult(new List<SmartMoneyStructure>());

        public Task<TechnicalReplayResponse> GetReplayAsync(
            string symbol, string timeframe, long asOfTimeMs, int lookbackBars, CancellationToken ct = default)
        {
            var indicator = new TechnicalLayerEnvelopeDto<TechnicalIndicatorReplayDto>
            {
                LayerKey = "technicalIndicators",
                Availability = "available",
                Lineage = new TechnicalLayerLineageDto
                {
                    ModuleContractVersion = contract.ContractVersion,
                    ModuleContractSha256 = contract.Sha256,
                    CalculationVersion = "finite-window-indicator-events-v1",
                    AvailableTimeMs = asOfTimeMs,
                    SourceStartTimeMs = asOfTimeMs - 60 * 3_600_000L,
                    SourceEndTimeMs = asOfTimeMs,
                    SourceCandleCount = 61
                },
                Payload = new TechnicalIndicatorReplayDto
                {
                    AvailableTimeMs = asOfTimeMs,
                    Events = eventAtEveryClose ? [new("EMA_BULL_CROSS", 1, 1)] : []
                }
            };
            return Task.FromResult(new TechnicalReplayResponse
            {
                EffectiveAsOfTimeMs = asOfTimeMs,
                Layers = new TechnicalReplayLayersDto { Indicators = indicator }
            });
        }
    }
}
