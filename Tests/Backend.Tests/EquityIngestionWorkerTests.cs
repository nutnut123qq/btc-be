using Microsoft.Extensions.Configuration;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

/// <summary>
/// EQX-A1 worker tests: session targeting (15:15 local, restart recovery), bounded per-session
/// retries → gave-up, isolation between symbols, lane-lock contention, data-plane status kept
/// apart from the heartbeat outcome. Pipeline faked for scheduling tests; one end-to-end test
/// drives the real <see cref="EquityLanePipeline"/> against EF InMemory + a fake VCI.
/// </summary>
[Collection(EquityLaneLockCollection.Name)]
public class EquityIngestionWorkerTests
{
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakePipeline : IEquityLanePipeline
    {
        public Func<string, long, int, EquityLaneRunResult> Behaviour { get; set; } =
            (_, _, _) => new EquityLaneRunResult(EquityLaneRunOutcome.Completed, "ok");
        public List<(string Symbol, long Session)> Calls { get; } = [];

        public Task<EquityLaneRunResult> RunAsync(InstrumentDescriptor instrument, long sessionOpenTimeMs,
            CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((instrument.Symbol, sessionOpenTimeMs));
            var n = Calls.Count(c => c.Symbol == instrument.Symbol);
            return Task.FromResult(Behaviour(instrument.Symbol, sessionOpenTimeMs, n));
        }
    }

    private static DateTimeOffset Local(int y, int m, int d, int hh, int mm) =>
        new(y, m, d, hh, mm, 0, TimeSpan.FromHours(7));

    private static (EquityIngestionWorker Worker, FakePipeline Pipeline, EquityIngestionStatusStore Store, TestTimeProvider Clock)
        NewWorker(DateTimeOffset now, EquityIngestionOptions? options = null, FakePipeline? pipeline = null,
            EquityIngestionStatusStore? store = null)
    {
        pipeline ??= new FakePipeline();
        store ??= new EquityIngestionStatusStore();
        var services = new ServiceCollection();
        services.AddSingleton<IEquityLanePipeline>(pipeline);
        var provider = services.BuildServiceProvider();
        var clock = new TestTimeProvider(now);
        var worker = new EquityIngestionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(options ?? new EquityIngestionOptions { Enabled = true, MaxAttemptsPerSession = 3 }),
            store, new VietnamMarketCalendar(), timeProvider: clock);
        return (worker, pipeline, store, clock);
    }

    private static readonly long Wed = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 14));
    private static readonly long Tue = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 13));

    [Fact]
    public void TargetSession_IsTodayAfterRunAfter_ElsePreviousSession()
    {
        var (worker, _, _, _) = NewWorker(Local(2026, 10, 14, 16, 0));
        Assert.Equal(Wed, worker.TargetSessionOpenMs(Local(2026, 10, 14, 15, 15).ToUnixTimeMilliseconds()));
        Assert.Equal(Tue, worker.TargetSessionOpenMs(Local(2026, 10, 14, 15, 0).ToUnixTimeMilliseconds()));
        Assert.Equal(Tue, worker.TargetSessionOpenMs(Local(2026, 10, 14, 9, 0).ToUnixTimeMilliseconds()));
        // Saturday → Friday's session.
        Assert.Equal(VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 16)),
            worker.TargetSessionOpenMs(Local(2026, 10, 17, 18, 0).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void EffectiveInstruments_AreConfiguredIngestionSymbolsOnly()
    {
        var (worker, _, _, _) = NewWorker(Local(2026, 10, 14, 16, 0),
            new EquityIngestionOptions { Enabled = true, Symbols = ["BTCUSDT", "bid", "NOPE", "VNINDEX"] });
        Assert.Equal(["BID", "VNINDEX"], worker.EffectiveInstruments().Select(i => i.Symbol).ToArray());
        var (all, _, _, _) = NewWorker(Local(2026, 10, 14, 16, 0));
        Assert.Equal(["MBB", "BID", "VCB", "CTG", "VNINDEX"], all.EffectiveInstruments().Select(i => i.Symbol).ToArray());
    }

    [Fact]
    public void Options_SymbolsBindFromConfig_NarrowInsteadOfAppending()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EquityIngestion:Enabled"] = "true",
                ["EquityIngestion:Symbols:0"] = "MBB",
            })
            .Build();
        var services = new ServiceCollection();
        services.Configure<EquityIngestionOptions>(config.GetSection(EquityIngestionOptions.SectionName));
        var bound = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<EquityIngestionOptions>>().Value;

        Assert.Equal(["MBB"], bound.Symbols);
        var (worker, _, _, _) = NewWorker(Local(2026, 10, 14, 16, 0), bound);
        Assert.Equal(["MBB"], worker.EffectiveInstruments().Select(i => i.Symbol).ToArray());
        Assert.Empty(new EquityIngestionOptions().Symbols); // default = all, via EffectiveInstruments
    }

    [Fact]
    public async Task LateProvider_ThenRetrySucceeds_ThenSessionIsNotRerun()
    {
        var (worker, pipeline, store, _) = NewWorker(Local(2026, 10, 14, 16, 0),
            new EquityIngestionOptions { Enabled = true, Symbols = ["BID"], MaxAttemptsPerSession = 3 });
        pipeline.Behaviour = (_, _, n) => n == 1
            ? new EquityLaneRunResult(EquityLaneRunOutcome.PendingProvider, "not served yet")
            : new EquityLaneRunResult(EquityLaneRunOutcome.Completed, "ok");

        var first = await worker.RunOnceAsync(default);
        Assert.Equal(EquityIngestionStatuses.PendingProvider, store.Get("BID")!.Status);
        Assert.Equal(1, store.Get("BID")!.Attempts);
        // Data-plane lateness is never a process failure (pitfall #40).
        Assert.Equal(WorkerCycleOutcome.Partial, first.Outcome);

        var second = await worker.RunOnceAsync(default);
        Assert.Equal(EquityIngestionStatuses.UpToDate, store.Get("BID")!.Status);
        Assert.Equal(Wed, store.Get("BID")!.SessionOpenTimeMs);
        Assert.Equal(WorkerCycleOutcome.Succeeded, second.Outcome);

        var third = await worker.RunOnceAsync(default);
        Assert.Equal(WorkerCycleOutcome.Idle, third.Outcome);
        Assert.Equal(2, pipeline.Calls.Count);
        Assert.All(pipeline.Calls, c => Assert.Equal(Wed, c.Session));
    }

    [Fact]
    public async Task RetriesExhausted_GaveUp_UntilNextSession()
    {
        var (worker, pipeline, store, clock) = NewWorker(Local(2026, 10, 14, 16, 0),
            new EquityIngestionOptions { Enabled = true, Symbols = ["VCB"], MaxAttemptsPerSession = 3 });
        pipeline.Behaviour = (_, _, _) => new EquityLaneRunResult(EquityLaneRunOutcome.Failed, "provider 503");

        for (var i = 0; i < 5; i++) await worker.RunOnceAsync(default);

        Assert.Equal(3, pipeline.Calls.Count); // bounded: no attempts after gave-up
        var state = store.Get("VCB")!;
        Assert.Equal(EquityIngestionStatuses.GaveUp, state.Status);
        Assert.Equal(3, state.Attempts);
        Assert.Equal("provider 503", state.Detail);

        // Next trading session resets the budget.
        clock.Now = Local(2026, 10, 15, 16, 0);
        pipeline.Behaviour = (_, _, _) => new EquityLaneRunResult(EquityLaneRunOutcome.Completed, "ok");
        await worker.RunOnceAsync(default);
        Assert.Equal(4, pipeline.Calls.Count);
        Assert.Equal(EquityIngestionStatuses.UpToDate, store.Get("VCB")!.Status);
        Assert.Equal(1, store.Get("VCB")!.Attempts);
    }

    [Fact]
    public async Task RestartRecovery_FreshProcessRunsLatestFinalizedSession()
    {
        // Process restarted at 10:00 on Wednesday: nothing in memory, so the first tick
        // recovers Tuesday's (latest finalized) session for every symbol.
        var (worker, pipeline, store, _) = NewWorker(Local(2026, 10, 14, 10, 0));

        await worker.RunOnceAsync(default);

        Assert.Equal(5, pipeline.Calls.Count);
        Assert.All(pipeline.Calls, c => Assert.Equal(Tue, c.Session));
        Assert.All(store.Snapshot(), s => Assert.Equal(EquityIngestionStatuses.UpToDate, s.Status));
    }

    [Fact]
    public async Task OneSymbolFailing_DoesNotBlockOthers()
    {
        var (worker, pipeline, store, _) = NewWorker(Local(2026, 10, 14, 16, 0));
        pipeline.Behaviour = (symbol, _, _) => symbol == "MBB"
            ? throw new InvalidOperationException("boom")
            : new EquityLaneRunResult(EquityLaneRunOutcome.Completed, "ok");

        var report = await worker.RunOnceAsync(default);

        Assert.Equal(5, pipeline.Calls.Count);
        Assert.Equal(EquityIngestionStatuses.Failed, store.Get("MBB")!.Status);
        Assert.Contains("boom", store.Get("MBB")!.Detail);
        foreach (var symbol in new[] { "BID", "VCB", "CTG", "VNINDEX" })
            Assert.Equal(EquityIngestionStatuses.UpToDate, store.Get(symbol)!.Status);
        Assert.Equal(WorkerCycleOutcome.Partial, report.Outcome);
        Assert.Equal(1, report.Failed);
        Assert.Equal(4, report.Succeeded);
    }

    [Fact]
    public async Task LaneBusy_SkipsSymbolThisTick_ThenRunsAfterRelease()
    {
        var (worker, pipeline, store, _) = NewWorker(Local(2026, 10, 14, 16, 0),
            new EquityIngestionOptions { Enabled = true, Symbols = ["CTG", "BID"] });
        using (var lease = EquityLaneLocks.TryAcquire("CTG"))
        {
            Assert.NotNull(lease);
            var report = await worker.RunOnceAsync(default);
            Assert.Contains("CTG=lane-busy", report.Detail);
            Assert.DoesNotContain(pipeline.Calls, c => c.Symbol == "CTG");
            Assert.Null(store.Get("CTG"));
            Assert.Equal(EquityIngestionStatuses.UpToDate, store.Get("BID")!.Status);
        }

        await worker.RunOnceAsync(default);
        Assert.Contains(pipeline.Calls, c => c.Symbol == "CTG");
        Assert.Equal(EquityIngestionStatuses.UpToDate, store.Get("CTG")!.Status);
        Assert.False(EquityLaneLocks.IsBusy("CTG")); // worker released its lease
    }

    [Fact]
    public async Task DisabledProviderLane_ReportsDisabled_WithoutBurningAttempts()
    {
        var (worker, pipeline, store, _) = NewWorker(Local(2026, 10, 14, 16, 0),
            new EquityIngestionOptions { Enabled = true, Symbols = ["BID"], MaxAttemptsPerSession = 2 });
        pipeline.Behaviour = (_, _, _) => new EquityLaneRunResult(EquityLaneRunOutcome.Disabled, "off");

        for (var i = 0; i < 4; i++) await worker.RunOnceAsync(default);

        Assert.Equal(EquityIngestionStatuses.Disabled, store.Get("BID")!.Status);
        Assert.Equal(0, store.Get("BID")!.Attempts);
    }

    [Fact]
    public async Task WorkerDisabled_ExitsImmediately_AndStaysInactive()
    {
        var store = new EquityIngestionStatusStore();
        var (worker, pipeline, _, _) = NewWorker(Local(2026, 10, 14, 16, 0),
            new EquityIngestionOptions { Enabled = false }, store: store);

        await worker.StartAsync(default);
        await worker.ExecuteTask!;

        Assert.False(store.WorkerActive);
        Assert.Empty(pipeline.Calls);
    }

    // --- real pipeline, end to end -------------------------------------------------

    /// <summary>Fake VCI per timeframe: the last countBack bars with open ≤ to.</summary>
    private sealed class SeriesVci : IVciOhlcvClient
    {
        public List<ProviderOhlcvBar> Bars { get; } = [];
        public List<ProviderOhlcvBar> HourlyBars { get; } = [];

        public Task<IReadOnlyList<ProviderOhlcvBar>> GetBarsAsync(string symbol, string timeframe,
            long toUnixSeconds, int countBack, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderOhlcvBar> slice = (timeframe == "1h" ? HourlyBars : Bars)
                .Where(b => b.OpenTimeMs <= toUnixSeconds * 1000L)
                .OrderBy(b => b.OpenTimeMs).TakeLast(countBack).ToArray();
            return Task.FromResult(slice);
        }

        public void AddSession(DateOnly date, decimal px)
        {
            Bars.Add(new ProviderOhlcvBar(VietnamMarketCalendar.DailyOpenUtcMs(date), px, px + 300, px - 300, px + 100, 1_000_000m));
            foreach (var open in VietnamMarketCalendar.HourlyOpensUtcMs(date))
                HourlyBars.Add(new ProviderOhlcvBar(open, px, px + 200, px - 200, px + 50, 200_000m));
        }
    }

    /// <summary>
    /// EQTECH-1 H: BID now declares 1h, so the same tick also ingests and recomputes BID 1h, while the
    /// journal stays 1d-only (CK_MbbSignalObservations_Scope; InMemory would accept a 1h row, so the
    /// single-row assertion is what catches a pipeline that observes every active timeframe).
    /// </summary>
    [Fact]
    public async Task RealPipeline_LateProviderThenSuccess_RecomputesAndObservesBid()
    {
        var dbName = nameof(RealPipeline_LateProviderThenSuccess_RecomputesAndObservesBid);
        var cal = new VietnamMarketCalendar([]);
        var clock = new TestTimeProvider(Local(2026, 10, 14, 16, 0));
        var vci = new SeriesVci();
        var dates = cal.EnumerateTradingDates(new DateOnly(2026, 4, 1), new DateOnly(2026, 10, 13));
        var i = 0;
        foreach (var d in dates)
            vci.AddSession(d, 40_000m + (i++ % 7) * 150m);

        var ingestionOptions = new MbbIngestionOptions
        {
            Enabled = true, LatestCountBack = 200, BatchCountBack = 500, RequestDelayMs = 0,
            GapFillStartLocal = "2026-04-01", NonTradingDates = [], HourlyBackfillStartLocal = "2026-04-01",
            DailyBackfillStartBySymbol = new(StringComparer.OrdinalIgnoreCase) { ["BID"] = "2026-04-01" },
        };
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(cal);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IVciOhlcvClient>(vci);
        services.AddScoped(sp => new MbbIngestionService(sp.GetRequiredService<AppDbContext>(), vci,
            ingestionOptions, cal, clock));
        services.AddScoped(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var opts = OptionsFactory.Create(new IndexingOptions());
            return new MbbTechnicalRecomputeService(db,
                new CandlePatternIndexer(db, new FakeBinanceKlinesService(), opts),
                new TechnicalIndicatorIndexer(db, NullLogger<TechnicalIndicatorIndexer>.Instance, opts),
                cal);
        });
        services.AddScoped(sp => new MbbDecisionService(sp.GetRequiredService<AppDbContext>(), cal, timeProvider: clock));
        services.AddScoped(sp => new MbbSignalObservationService(sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<MbbDecisionService>(), cal, clock));
        services.AddScoped<IEquityLanePipeline, EquityLanePipeline>();
        var provider = services.BuildServiceProvider();
        var store = new EquityIngestionStatusStore();
        var worker = new EquityIngestionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new EquityIngestionOptions { Enabled = true, Symbols = ["BID"] }),
            store, cal, timeProvider: clock);

        // 16:00 Wed: the provider has history through Tuesday only — late for today's bar.
        await worker.RunOnceAsync(default);
        Assert.Equal(EquityIngestionStatuses.PendingProvider, store.Get("BID")!.Status);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.InstrumentCandles.CountAsync(c => c.Symbol == "BID") > 100);
            Assert.Empty(await db.MbbSignalObservations.ToListAsync());
        }

        // Provider publishes Wednesday's bars; the next tick completes the whole chain.
        vci.AddSession(new DateOnly(2026, 10, 14), 40_500m);
        clock.Now = Local(2026, 10, 14, 16, 15);
        await worker.RunOnceAsync(default);

        var state = store.Get("BID")!;
        Assert.Equal(EquityIngestionStatuses.UpToDate, state.Status);
        Assert.Contains("recompute 1h", state.Detail);
        Assert.Contains("recompute 1d", state.Detail);
        Assert.Contains("observe 1d recorded", state.Detail);
        Assert.DoesNotContain("observe 1h", state.Detail);
        var lastHour = VietnamMarketCalendar.HourlyOpensUtcMs(new DateOnly(2026, 10, 14))[^1];
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.TechnicalIndicators.AnyAsync(t => t.Symbol == "BID" && t.Timeframe == "1d" && t.OpenTimeMs == Wed));
            Assert.True(await db.TechnicalIndicators.AnyAsync(t => t.Symbol == "BID" && t.Timeframe == "1h" && t.OpenTimeMs == lastHour));
            var obs = await db.MbbSignalObservations.SingleAsync();
            Assert.Equal($"equity-observe/BID/1d/{Wed}", obs.DecisionId);
            Assert.Equal(vci.HourlyBars.Count,
                await db.InstrumentCandles.CountAsync(c => c.Symbol == "BID" && c.Timeframe == "1h"));
        }
    }
}
