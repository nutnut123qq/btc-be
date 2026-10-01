using Backend.Data;
using Backend.Controllers;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Backend.Tests;

public class SmartMoneyServiceTests
{
    [Fact]
    public void DetectStructures_UsesCausalAvailabilityAndConsumesBrokenPivotOnce()
    {
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 9, 11, 8, 10),
            Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11),
            Bar(4, 11, 12, 10, 11),
            Bar(5, 11, 17, 11, 16),
            Bar(6, 16, 18, 12, 17)
        };

        var events = SmartMoneyService.DetectStructures(rows, "BTCUSDT", "1h");
        var swing = Assert.Single(events.Where(x => x.EventType == "SWING_HIGH"));
        Assert.Equal(rows[2].OpenTimeMs, swing.OriginTimeMs);
        Assert.Equal(rows[4].CloseTimeMs, swing.AvailableTimeMs);

        var broken = Assert.Single(events.Where(x => x.EventType is "BOS_BULL" or "CHOCH_BULL"));
        Assert.Equal(rows[5].CloseTimeMs, broken.AvailableTimeMs);
        Assert.Equal(swing.OriginTimeMs, broken.ReferenceTimeMs);
    }

    [Fact]
    public void DetectStructures_FvgIsAvailableAtFinalDefiningBar()
    {
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 10, 11, 9, 10),
            Bar(2, 12, 13, 11, 12)
        };

        var fvg = Assert.Single(SmartMoneyService.DetectStructures(rows, "BTCUSDT", "1h")
            .Where(x => x.EventType == "FVG_BULL"));
        Assert.Equal(rows[1].OpenTimeMs, fvg.OriginTimeMs);
        Assert.Equal(rows[2].CloseTimeMs, fvg.AvailableTimeMs);
    }

    [Fact]
    public void DetectStructures_SequentialReplayMatchesBatchEventsAvailableAtEachCutoff()
    {
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11), Bar(5, 11, 17, 11, 16),
            Bar(6, 16, 18, 12, 17), Bar(7, 17, 18, 13, 14)
        };
        var batch = SmartMoneyService.DetectStructures(rows, "BTCUSDT", "1h");

        for (var count = 3; count <= rows.Length; count++)
        {
            var cutoff = rows[count - 1].CloseTimeMs;
            var replayKeys = SmartMoneyService.DetectStructures(rows.Take(count).ToArray(), "BTCUSDT", "1h")
                .Select(EventKey).Order().ToArray();
            var batchKeys = batch.Where(x => x.AvailableTimeMs <= cutoff)
                .Select(EventKey).Order().ToArray();
            Assert.Equal(batchKeys, replayKeys);
        }
    }

    [Fact]
    public async Task GetStructures_RetryDoesNotDuplicateLogicalEvents()
    {
        await using var db = CreateDb();
        db.Klines.AddRange(new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11), Bar(5, 11, 17, 11, 16)
        });
        await db.SaveChangesAsync();
        var service = new SmartMoneyService(db);

        await service.GetSmartMoneyStructuresAsync("BTCUSDT", "1h", 100);
        var firstCount = await db.SmartMoneyStructures.CountAsync();
        await service.GetSmartMoneyStructuresAsync("BTCUSDT", "1h", 100);

        Assert.True(firstCount > 0);
        Assert.Equal(firstCount, await db.SmartMoneyStructures.CountAsync());
    }

    [Fact]
    public async Task Replay_DoesNotExposePivotBeforeConfirmationCandleFinalizes()
    {
        await using var db = CreateDb();
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 9, 11, 8, 10),
            Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11),
            Bar(4, 11, 12, 10, 11)
        };
        db.Klines.AddRange(rows);
        await db.SaveChangesAsync();
        var service = new SmartMoneyService(db);

        var beforeConfirmation = await service.GetReplayAsync("BTCUSDT", "1h", rows[3].CloseTimeMs, 100);
        var afterConfirmation = await service.GetReplayAsync("BTCUSDT", "1h", rows[4].CloseTimeMs, 100);

        Assert.DoesNotContain(beforeConfirmation.Events, x => x.EventType == "SWING_HIGH" && x.OriginTimeMs == rows[2].OpenTimeMs);
        var swing = Assert.Single(afterConfirmation.Events.Where(x => x.EventType == "SWING_HIGH"));
        Assert.Equal(rows[4].CloseTimeMs, swing.AvailableTimeMs);
        Assert.All(swing.SourceCandles, x => Assert.True(x.CloseTimeMs <= afterConfirmation.EffectiveAsOfTimeMs));
    }

    [Fact]
    public async Task Replay_FutureBarsDoNotRewriteAnAlreadyKnownEvent()
    {
        await using var db = CreateDb();
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11), Bar(5, 11, 17, 11, 16),
            Bar(6, 16, 18, 12, 17)
        };
        db.Klines.AddRange(rows);
        await db.SaveChangesAsync();
        var service = new SmartMoneyService(db);

        var firstReplay = await service.GetReplayAsync("BTCUSDT", "1h", rows[4].CloseTimeMs, 100);
        var laterReplay = await service.GetReplayAsync("BTCUSDT", "1h", rows[6].CloseTimeMs, 100);
        var first = Assert.Single(firstReplay.Events.Where(x => x.EventType == "SWING_HIGH"));
        var later = Assert.Single(laterReplay.Events.Where(x => x.EventId == first.EventId));

        Assert.Equal(first.OriginTimeMs, later.OriginTimeMs);
        Assert.Equal(first.AvailableTimeMs, later.AvailableTimeMs);
        Assert.Equal(first.ReferenceTimeMs, later.ReferenceTimeMs);
        Assert.Equal(first.CalculationVersion, later.CalculationVersion);
        Assert.Equal(first.SourceCandles.Select(x => (x.Role, x.OpenTimeMs)), later.SourceCandles.Select(x => (x.Role, x.OpenTimeMs)));
    }

    [Fact]
    public async Task Replay_SlidingLookbackDoesNotDropEventThatRemainsInsideWindow()
    {
        await using var db = CreateDb();
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11), Bar(5, 11, 14, 10, 12)
        };
        db.Klines.AddRange(rows);
        await db.SaveChangesAsync();
        var service = new SmartMoneyService(db);

        var first = await service.GetReplayAsync("BTCUSDT", "1h", rows[4].CloseTimeMs, 5);
        var later = await service.GetReplayAsync("BTCUSDT", "1h", rows[5].CloseTimeMs, 5);
        var known = Assert.Single(first.Events.Where(x => x.EventType == "SWING_HIGH"));
        var stable = Assert.Single(later.Events.Where(x => x.EventId == known.EventId));

        Assert.Equal(known.SourceCandles.Select(x => (x.Role, x.OpenTimeMs)),
            stable.SourceCandles.Select(x => (x.Role, x.OpenTimeMs)));
        Assert.Equal(rows[1].OpenTimeMs, later.ReplayWindowStartTimeMs);
        Assert.Equal(5, later.SourceCandleCount);
        Assert.Equal(rows.Length, later.AnalysisCandleCount);
        Assert.Equal(rows.Skip(1).Select(x => x.OpenTimeMs), later.Candles.Select(x => x.OpenTimeMs));
        Assert.All(later.Candles, x => Assert.True(x.CloseTimeMs <= later.EffectiveAsOfTimeMs));
        Assert.All(later.Events.SelectMany(x => x.SourceCandles)
                .Where(x => x.OpenTimeMs >= later.ReplayWindowStartTimeMs),
            source => Assert.Contains(later.Candles, candle =>
                candle.OpenTimeMs == source.OpenTimeMs
                && candle.CloseTimeMs == source.CloseTimeMs
                && candle.Open == source.Open
                && candle.High == source.High
                && candle.Low == source.Low
                && candle.Close == source.Close
                && candle.Volume == source.Volume));
    }

    [Fact]
    public void DetectStructures_DoesNotBridgeMissingCandles()
    {
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 9, 11, 8, 10),
            Bar(3, 20, 21, 19, 20)
        };

        var events = SmartMoneyService.DetectStructures(rows, "BTCUSDT", "1h");

        Assert.Empty(events);
        var segment = SmartMoneyService.LatestContiguousSegment(rows, "1h");
        Assert.Single(segment);
        Assert.Equal(rows[2].OpenTimeMs, segment[0].OpenTimeMs);
    }

    [Fact]
    public async Task Replay_ExcludesMalformedDurationAndEarlierCausalState()
    {
        await using var db = CreateDb();
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 9, 11, 8, 10),
            Bar(2, 10, 12, 9, 11),
            Bar(3, 11, 13, 10, 12),
            Bar(4, 12, 14, 11, 13)
        };
        rows[2].CloseTimeMs--;
        db.Klines.AddRange(rows);
        await db.SaveChangesAsync();

        var replay = await new SmartMoneyService(db)
            .GetReplayAsync("BTCUSDT", "1h", rows[^1].CloseTimeMs, 100);

        Assert.Equal(rows[3].OpenTimeMs, replay.ContiguousSegmentStartTimeMs);
        Assert.Equal(2, replay.SourceCandleCount);
        Assert.Equal(rows.Skip(3).Select(x => x.OpenTimeMs), replay.Candles.Select(x => x.OpenTimeMs));
        Assert.Empty(replay.Events);
    }

    [Fact]
    public async Task Replay_FvgMitigationAppearsOnlyAfterMitigatingCandleCloses()
    {
        await using var db = CreateDb();
        var rows = new[]
        {
            Bar(0, 9, 10, 8, 9),
            Bar(1, 10, 11, 9, 10),
            Bar(2, 12, 13, 11, 12),
            Bar(3, 12, 13, 10.5m, 12),
            Bar(4, 12, 12.5m, 9, 10)
        };
        db.Klines.AddRange(rows);
        await db.SaveChangesAsync();
        var service = new SmartMoneyService(db);

        var before = await service.GetReplayAsync("BTCUSDT", "1h", rows[3].CloseTimeMs, 100);
        var after = await service.GetReplayAsync("BTCUSDT", "1h", rows[4].CloseTimeMs, 100);
        var active = Assert.Single(before.Events.Where(x => x.EventType == "FVG_BULL" && x.OriginTimeMs == rows[1].OpenTimeMs));
        var mitigated = Assert.Single(after.Events.Where(x => x.EventId == active.EventId));

        Assert.Equal("active", active.StateAtAsOf);
        Assert.Null(active.MitigatedAtMs);
        Assert.Equal("mitigated", mitigated.StateAtAsOf);
        Assert.Equal(rows[4].CloseTimeMs, mitigated.MitigatedAtMs);
        Assert.Null(mitigated.InvalidatedAtMs);
        Assert.Null(mitigated.InvalidationRule);
        Assert.Contains(mitigated.SourceCandles, x => x.Role == "fvg-mitigation" && x.OpenTimeMs == rows[4].OpenTimeMs);
    }

    [Fact]
    public async Task Replay_SingleBearishFvgMitigationDoesNotOverDequeue()
    {
        await using var db = CreateDb();
        var rows = new[]
        {
            Bar(0, 21, 22, 20, 21),
            Bar(1, 19, 19, 18, 18.5m),
            Bar(2, 16, 17, 15, 16),
            Bar(3, 17, 20, 16, 19)
        };
        db.Klines.AddRange(rows);
        await db.SaveChangesAsync();

        var replay = await new SmartMoneyService(db)
            .GetReplayAsync("BTCUSDT", "1h", rows[^1].CloseTimeMs, 100);
        var bearish = Assert.Single(replay.Events.Where(x =>
            x.EventType == "FVG_BEAR" && x.OriginTimeMs == rows[1].OpenTimeMs));

        Assert.Equal("mitigated", bearish.StateAtAsOf);
        Assert.Equal(rows[3].CloseTimeMs, bearish.MitigatedAtMs);
    }

    [Fact]
    public async Task Replay_OneCandleCanMitigateMultipleBearishFvgs()
    {
        await using var db = CreateDb();
        var rows = new[]
        {
            Bar(0, 21, 22, 20, 21),
            Bar(1, 19, 19, 18, 18.5m),
            Bar(2, 16.5m, 17, 16, 16.5m),
            Bar(3, 15.5m, 16, 15, 15.5m),
            Bar(4, 18, 21, 14, 20)
        };
        db.Klines.AddRange(rows);
        await db.SaveChangesAsync();

        var replay = await new SmartMoneyService(db)
            .GetReplayAsync("BTCUSDT", "1h", rows[^1].CloseTimeMs, 100);
        var bearish = replay.Events.Where(x => x.EventType == "FVG_BEAR").ToArray();

        Assert.Equal(2, bearish.Length);
        Assert.All(bearish, x =>
        {
            Assert.Equal("mitigated", x.StateAtAsOf);
            Assert.Equal(rows[4].CloseTimeMs, x.MitigatedAtMs);
        });
    }

    [Fact]
    public async Task Replay_SnapsEffectiveAsOfToLastFinalizedCandleAndDoesNotPersist()
    {
        await using var db = CreateDb();
        var finalized = new[]
        {
            Bar(0, 9, 10, 8, 9), Bar(1, 9, 11, 8, 10), Bar(2, 10, 15, 9, 12),
            Bar(3, 12, 13, 10, 11), Bar(4, 11, 12, 10, 11)
        };
        var stillOpen = Bar(5, 11, 17, 11, 16);
        db.Klines.AddRange(finalized.Append(stillOpen));
        await db.SaveChangesAsync();
        var service = new SmartMoneyService(db);
        var requested = stillOpen.CloseTimeMs - 1;

        var replay = await service.GetReplayAsync("BTCUSDT", "1h", requested, 100);

        Assert.Equal(requested, replay.RequestedAsOfTimeMs);
        Assert.Equal(finalized[^1].CloseTimeMs, replay.EffectiveAsOfTimeMs);
        Assert.Equal(finalized.Length, replay.SourceCandleCount);
        Assert.Equal(finalized.Length, replay.AnalysisCandleCount);
        Assert.Equal(finalized.Select(x => x.OpenTimeMs), replay.Candles.Select(x => x.OpenTimeMs));
        Assert.False(replay.Provenance.PersistedByReplay);
        Assert.Equal(0, await db.SmartMoneyStructures.CountAsync());
        Assert.All(replay.Events, x => Assert.True(x.AvailableTimeMs <= requested));
    }

    [Fact]
    public async Task ReplayCoverage_DistinguishesCheckpointProgressFromSparseEventMaterialization()
    {
        await using var db = CreateDb();
        var rows = Enumerable.Range(0, 5).Select(i => Bar(i, 100 + i, 102 + i, 99 + i, 101 + i)).ToArray();
        db.Klines.AddRange(rows);
        var contract = new BuiltInTechnicalModuleContractProvider();
        db.TechnicalEvidenceRebuildCheckpoints.Add(new TechnicalEvidenceRebuildCheckpoint
        {
            Symbol = "BTCUSDT", Timeframe = "1h",
            ModuleContractVersion = contract.ContractVersion,
            ModuleContractSha256 = contract.Sha256,
            LastProcessedCloseTimeMs = rows[^1].CloseTimeMs,
            Status = "complete"
        });
        db.TechnicalEvidenceRecords.Add(new TechnicalEvidenceRecord
        {
            Symbol = "BTCUSDT", Timeframe = "1h", LayerKey = "technicalIndicators",
            AsOfTimeMs = rows[^1].CloseTimeMs,
            ModuleContractVersion = contract.ContractVersion,
            ModuleContractSha256 = contract.Sha256,
            CalculationVersion = TechnicalReplayLayerService.IndicatorsVersion,
            Availability = "available", EnvelopeJson = "{}"
        });
        await db.SaveChangesAsync();

        var replay = await new SmartMoneyService(db, moduleContract: contract)
            .GetReplayAsync("BTCUSDT", "1h", rows[^1].CloseTimeMs, 5);

        var materialized = replay.Coverage.Single(x => x.LayerKey == "technicalIndicators");
        Assert.Equal("complete", materialized.CheckpointStatus);
        Assert.True(materialized.IsEventEnvelopeMaterializedAtAsOf);
        Assert.Equal("sparse_event_envelope_materialized", materialized.StorageStatus);
        Assert.All(replay.Coverage.Where(x => x.LayerKey != "technicalIndicators"), layer =>
        {
            Assert.Equal("complete", layer.CheckpointStatus);
            Assert.False(layer.IsEventEnvelopeMaterializedAtAsOf);
            Assert.Equal("on_demand_state_checkpoint_processed", layer.StorageStatus);
        });
    }

    [Theory]
    [InlineData("ETHUSDT", "4h", 1_700_000_000_000, "UNSUPPORTED_SYMBOL")]
    [InlineData("BTCUSDT", "15m", 1_700_000_000_000, "INACTIVE_TIMEFRAME")]
    [InlineData("BTCUSDT", "4h", 0, "INVALID_AS_OF")]
    [InlineData("BTCUSDT", "4h", 1_800_000_000_000, "INVALID_AS_OF")]
    public async Task ReplayController_RejectsInvalidScopeAndAsOf(
        string symbol,
        string timeframe,
        long asOfTimeMs,
        string expectedCode)
    {
        await using var db = CreateDb();
        var service = new SmartMoneyService(db);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        var controller = new SmartMoneyController(service, timeProvider: new FixedTimeProvider(now))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = Assert.IsType<BadRequestObjectResult>(
            await controller.Replay(symbol, timeframe, asOfTimeMs, 100));
        var error = Assert.IsType<ApiErrorEnvelope>(response.Value);

        Assert.Equal(expectedCode, error.Code);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSmartMoneyEndpoints_RejectUnsupportedSymbolAsBadRequest(bool detect)
    {
        await using var db = CreateDb();
        var controller = new SmartMoneyController(new SmartMoneyService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = detect
            ? await controller.Detect("ETHUSDT", "4h", 100)
            : await controller.GetStructures("ETHUSDT", "4h", 100);
        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        var error = Assert.IsType<ApiErrorEnvelope>(badRequest.Value);

        Assert.Equal("UNSUPPORTED_SYMBOL", error.Code);
    }

    private static string EventKey(SmartMoneyStructure x) =>
        $"{x.EventType}|{x.OriginTimeMs}|{x.AvailableTimeMs}|{x.ReferenceTimeMs}";

    private static Kline Bar(int hour, decimal open, decimal high, decimal low, decimal close)
    {
        const long baseTimeMs = 1_700_000_000_000;
        var openTime = baseTimeMs + hour * 3_600_000L;
        return new Kline
        {
            Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = openTime,
            CloseTimeMs = openTime + 3_599_999, Open = open, High = high, Low = low, Close = close,
            Volume = 100
        };
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AppDbContext(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
