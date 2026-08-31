using Backend.Controllers;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

public class Phase3ReliabilityTests
{
    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static KlinesIngestionWorker CreateWorker(TimeProvider? timeProvider = null) => new(
        null!, NullLogger<KlinesIngestionWorker>.Instance, OptionsFactory.Create(new KlinesIngestionOptions()),
        timeProvider: timeProvider);

    [Fact]
    public async Task EmptyGap_IsDeferredThenBecomesUnavailableAfterThirdAttempt()
    {
        await using var db = CreateDb();
        db.KlineGapStates.Add(new KlineGapState
        {
            Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = 0, EndOpenTimeMs = 0,
            MissingBars = 1, Status = KlineGapStatuses.Pending
        });
        await db.SaveChangesAsync();
        var state = await db.KlineGapStates.SingleAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var worker = CreateWorker(clock);

        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, true, default);
        Assert.Equal(KlineGapStatuses.Pending, state.Status);
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddHours(24), state.NextRetryAtUtc);
        Assert.Empty(await worker.GetDueGapStatesAsync(db, 10, default));

        clock.Advance(TimeSpan.FromHours(24));
        Assert.Single(await worker.GetDueGapStatesAsync(db, 10, default));
        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, true, default);
        Assert.Empty(await worker.GetDueGapStatesAsync(db, 10, default));
        clock.Advance(TimeSpan.FromHours(24));
        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, true, default);

        Assert.Equal(KlineGapStatuses.Unavailable, state.Status);
        Assert.Equal(3, state.AttemptCount);
        Assert.Null(state.NextRetryAtUtc);
    }

    [Fact]
    public async Task ThreeTransportFailuresRemainPendingAndDoNotCountAsUnavailableEvidence()
    {
        await using var db = CreateDb();
        db.KlineGapStates.Add(new KlineGapState
        {
            Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = 0, EndOpenTimeMs = 0,
            MissingBars = 1, Status = KlineGapStatuses.Pending
        });
        await db.SaveChangesAsync();
        var state = await db.KlineGapStates.SingleAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var worker = CreateWorker(clock);

        for (var i = 0; i < 3; i++)
        {
            await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, false, default, "Binance request failed");
            clock.Advance(TimeSpan.FromHours(24));
        }

        Assert.Equal(KlineGapStatuses.Pending, state.Status);
        Assert.Equal(0, state.AttemptCount);
        Assert.NotNull(state.NextRetryAtUtc);
    }

    [Fact]
    public async Task MixedTransportFailuresAndOneEmptyResponseRemainPending()
    {
        await using var db = CreateDb();
        db.KlineGapStates.Add(new KlineGapState
        {
            Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = 0, EndOpenTimeMs = 0,
            MissingBars = 1, Status = KlineGapStatuses.Pending
        });
        await db.SaveChangesAsync();
        var state = await db.KlineGapStates.SingleAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var worker = CreateWorker(clock);

        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, false, default, "Binance request failed");
        clock.Advance(TimeSpan.FromHours(24));
        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, false, default, "Binance request failed");
        clock.Advance(TimeSpan.FromHours(24));
        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, true, default);

        Assert.Equal(KlineGapStatuses.Pending, state.Status);
        Assert.Equal(1, state.AttemptCount);
    }

    [Fact]
    public async Task GapState_IsIdempotentAndTransitionsToFilled()
    {
        await using var db = CreateDb();
        var worker = CreateWorker();
        var gap = new KlinesIngestionWorker.Gap(0, 3_600_000, 2);
        await worker.PersistDetectedGapsAsync(db, "BTCUSDT", "1h", [gap], default);
        await worker.PersistDetectedGapsAsync(db, "BTCUSDT", "1h", [gap], default);
        var state = await db.KlineGapStates.SingleAsync();

        db.Klines.AddRange(CreateKline(0), CreateKline(3_600_000));
        await db.SaveChangesAsync();
        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, false, default);

        Assert.Equal(KlineGapStatuses.Filled, state.Status);
        Assert.Equal(0, state.MissingBars);
    }

    [Fact]
    public async Task PartialBackfill_DoesNotCreateOverlappingChildGapState()
    {
        await using var db = CreateDb();
        var worker = CreateWorker();
        await worker.PersistDetectedGapsAsync(db, "BTCUSDT", "1h",
            [new KlinesIngestionWorker.Gap(0, 7_200_000, 3)], default);

        db.Klines.Add(CreateKline(0));
        await db.SaveChangesAsync();
        var state = await db.KlineGapStates.SingleAsync();
        await worker.UpdateGapAfterAttemptAsync(db, state.Id, 3_600_000, false, default);
        await worker.PersistDetectedGapsAsync(db, "BTCUSDT", "1h",
            [new KlinesIngestionWorker.Gap(3_600_000, 7_200_000, 2)], default);

        Assert.Single(await db.KlineGapStates.ToListAsync());
        Assert.Equal(2, state.MissingBars);
    }

    [Fact]
    public async Task RecurringExactGap_ReopensFilledStateWithoutUniqueInsert()
    {
        await using var db = CreateDb();
        var worker = CreateWorker();
        var gap = new KlinesIngestionWorker.Gap(0, 0, 1);
        await worker.PersistDetectedGapsAsync(db, "BTCUSDT", "1h", [gap], default);
        var state = await db.KlineGapStates.SingleAsync();
        state.Status = KlineGapStatuses.Filled;
        await db.SaveChangesAsync();

        await worker.PersistDetectedGapsAsync(db, "BTCUSDT", "1h", [gap], default);

        Assert.Single(await db.KlineGapStates.ToListAsync());
        Assert.Equal(KlineGapStatuses.Pending, state.Status);
        Assert.Equal(0, state.AttemptCount);
    }

    [Fact]
    public async Task ManualRetry_ResetsUnavailableGapButRejectsFilledGap()
    {
        await using var db = CreateDb();
        var gap = new KlineGapState
        {
            Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = 0, EndOpenTimeMs = 0,
            MissingBars = 1, Status = KlineGapStatuses.Unavailable, AttemptCount = 3
        };
        db.KlineGapStates.Add(gap);
        await db.SaveChangesAsync();
        var audit = new FakeAuditService();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));
        var controller = CreateMarketController(db, audit, clock);

        var ok = Assert.IsType<OkObjectResult>(await controller.RetryDataGap(gap.Id));
        var response = Assert.IsType<GapRetryResponse>(ok.Value);
        Assert.Equal(KlineGapStatuses.Pending, response.Status);
        Assert.Equal(0, response.AttemptCount);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, response.UpdatedAtUtc);
        Assert.Equal("BTCUSDT", audit.InvalidatedSymbol);

        gap.Status = KlineGapStatuses.Filled;
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await controller.RetryDataGap(gap.Id));
    }

    [Fact]
    public async Task Workers_ReturnsPersistedHealthyAndMissingHeartbeatStates()
    {
        await using var db = CreateDb();
        db.WorkerHeartbeats.Add(new WorkerHeartbeat
        {
            WorkerName = nameof(KlinesIngestionWorker), Status = "Succeeded",
            LastStartedAtUtc = DateTime.UtcNow.AddMinutes(-1), LastSucceededAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var controller = new HealthController(db, NullLogger<HealthController>.Instance);

        var ok = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        var response = Assert.IsType<WorkerHealthResponse>(ok.Value);
        Assert.Equal("healthy", response.Workers.Single(x => x.Name == nameof(KlinesIngestionWorker)).Status);
        Assert.Equal("never", response.Workers.Single(x => x.Name == nameof(IndexingBackgroundWorker)).Status);

        var heartbeat = await db.WorkerHeartbeats.SingleAsync();
        heartbeat.Status = "Failed";
        heartbeat.LastFailedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var failed = Assert.IsType<OkObjectResult>((await controller.Workers(default)).Result);
        Assert.Equal("failed", Assert.IsType<WorkerHealthResponse>(failed.Value).Workers
            .Single(x => x.Name == nameof(KlinesIngestionWorker)).Status);
    }

    [Fact]
    public void Model_HasGapUniquenessAndDueRetryIndex()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(KlineGapState));
        Assert.NotNull(entity);
        var indexes = entity!.GetIndexes().Select(x => string.Join(",", x.Properties.Select(p => p.Name))).ToArray();
        Assert.Contains("Symbol,Timeframe,StartOpenTimeMs,EndOpenTimeMs", indexes);
        Assert.Contains("Status,NextRetryAtUtc", indexes);
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique
            && x.Properties.Select(p => p.Name).SequenceEqual(["Symbol", "Timeframe", "StartOpenTimeMs", "EndOpenTimeMs"]));
    }

    [Fact]
    public void InsertBatch_OnlySwallowsPostgresUniqueViolation()
    {
        Assert.False(KlinesIngestionWorker.IsUniqueViolation(new DbUpdateException("non-unique failure")));
    }

    [Fact]
    public async Task DeferredGap_SurvivesNewDbContext()
    {
        var name = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;
        long id;
        await using (var first = new AppDbContext(options))
        {
            var state = new KlineGapState
            {
                Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = 0, EndOpenTimeMs = 0,
                MissingBars = 1, Status = KlineGapStatuses.Pending,
                NextRetryAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            first.KlineGapStates.Add(state);
            await first.SaveChangesAsync();
            id = state.Id;
        }
        await using var restarted = new AppDbContext(options);
        var loaded = await restarted.KlineGapStates.SingleAsync(x => x.Id == id);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), loaded.NextRetryAtUtc);
    }

    [Fact]
    public async Task Reconcile_MarksUnavailableGapFilledWithoutRetry()
    {
        await using var db = CreateDb();
        db.KlineGapStates.Add(new KlineGapState
        {
            Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = 0, EndOpenTimeMs = 0,
            MissingBars = 1, Status = KlineGapStatuses.Unavailable, AttemptCount = 3
        });
        db.Klines.Add(CreateKline(0));
        await db.SaveChangesAsync();

        var updated = await CreateWorker().ReconcileResolvedGapStatesAsync(db, "BTCUSDT", "1h", 3_600_000, default);

        Assert.Equal(1, updated);
        Assert.Equal(KlineGapStatuses.Filled, (await db.KlineGapStates.SingleAsync()).Status);
    }

    private static Kline CreateKline(long openTimeMs) => new()
    {
        Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 3_599_999, Open = 1, High = 1, Low = 1, Close = 1
    };

    private static MarketController CreateMarketController(AppDbContext db, IDataAuditService audit, TimeProvider? timeProvider = null)
    {
        var controller = new MarketController(null!, null!, null!, null!, null!, null!, null!, audit, db,
            NullLogger<MarketController>.Instance, timeProvider);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed class FakeAuditService : IDataAuditService
    {
        public string? InvalidatedSymbol { get; private set; }
        public Task<DataAuditResponse> AuditAsync(string symbol, bool includeInventory = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void Invalidate(string symbol) => InvalidatedSymbol = symbol;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
