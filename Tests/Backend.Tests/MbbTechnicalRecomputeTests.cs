using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

/// <summary>
/// MBB-03 tests: technical artifacts rebuilt from InstrumentCandles (never Klines),
/// symbol isolation vs BTC, session-gap-aware indicator segmentation, fail-closed
/// symbol/timeframe validation, and full-rebuild idempotency. EF InMemory only —
/// no provider HTTP calls.
/// </summary>
public class MbbTechnicalRecomputeTests
{
    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name).Options);

    private static IndexingOptions TestIndexingOptions() => new()
    {
        CandlePatternBatchSize = 100,
        VolumeStatsBatchSize = 100,
        TechnicalIndicatorsBatchSize = 100,
        PatternSequenceBatchSize = 100,
        WindowVectorBatchSize = 100,
    };

    private static MbbTechnicalRecomputeService NewService(AppDbContext db)
    {
        var opts = OptionsFactory.Create(TestIndexingOptions());
        return new MbbTechnicalRecomputeService(
            db,
            new CandlePatternIndexer(db, new FakeBinanceKlinesService(), opts),
            new WindowVectorIndexer(db, opts),
            new CandleVolumeIndexer(db, NullLogger<CandleVolumeIndexer>.Instance, opts),
            new TechnicalIndicatorIndexer(db, NullLogger<TechnicalIndicatorIndexer>.Instance, opts),
            new CandlePatternSequenceIndexer(db, NullLogger<CandlePatternSequenceIndexer>.Instance, opts),
            new VietnamMarketCalendar());
    }

    private static InstrumentCandle Candle(string timeframe, long openMs, long closeMs,
        decimal o, decimal h, decimal l, decimal c, decimal v) => new()
    {
        Symbol = "MBB",
        Timeframe = timeframe,
        OpenTimeMs = openMs,
        CloseTimeMs = closeMs,
        Open = o,
        High = h,
        Low = l,
        Close = c,
        Volume = v,
        Provider = "VCI",
        PriceBasis = "adjusted",
        Venue = "HOSE",
        SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// Seed <paramref name="tradingDays"/> daily MBB candles starting at a Monday.
    /// Every 10th bar is a flat doji so the recognizer deterministically emits
    /// enough patterns to build sequences.
    /// </summary>
    private static async Task<int> SeedDailyCandlesAsync(
        AppDbContext db, int tradingDays, DateOnly start, string symbol = "MBB")
    {
        var cal = new VietnamMarketCalendar();
        var dates = cal.EnumerateTradingDates(start, start.AddDays(tradingDays * 2))
            .Take(tradingDays).ToArray();

        decimal px = 20_000m;
        for (var i = 0; i < dates.Length; i++)
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(dates[i]);
            var close = cal.SessionCloseUtcMs("1d", open);
            var o = px;
            var c = px + (i % 7 - 3) * 25m;
            var h = Math.Max(o, c) + 50m;
            var l = Math.Min(o, c) - 50m;
            if (i % 10 == 9)
            {
                // Flat bar: O==H==L==C => recognizer emits Doji.
                c = o;
                h = o;
                l = o;
            }
            var row = Candle("1d", open, close, o, h, l, c, 1_000_000m + i * 1_000m);
            row.Symbol = symbol;
            db.InstrumentCandles.Add(row);
            px = c;
        }

        await db.SaveChangesAsync();
        return dates.Length;
    }

    /// <summary>Seed HOSE-session hourly candles: 5 expected opens per trading day.</summary>
    private static async Task<int> SeedHourlyCandlesAsync(
        AppDbContext db, int tradingDays, DateOnly start)
    {
        var cal = new VietnamMarketCalendar();
        var dates = cal.EnumerateTradingDates(start, start.AddDays(tradingDays * 2))
            .Take(tradingDays).ToArray();

        decimal px = 20_000m;
        var i = 0;
        foreach (var d in dates)
        {
            foreach (var open in VietnamMarketCalendar.HourlyOpensUtcMs(d))
            {
                var close = cal.SessionCloseUtcMs("1h", open);
                var o = px;
                var c = px + (i % 5 - 2) * 20m;
                var h = Math.Max(o, c) + 30m;
                var l = Math.Min(o, c) - 30m;
                db.InstrumentCandles.Add(
                    Candle("1h", open, close, o, h, l, c, 500_000m + i * 500m));
                px = c;
                i++;
            }
        }

        await db.SaveChangesAsync();
        return dates.Length * 5;
    }

    [Fact]
    public async Task Recompute_Daily_WritesAllArtifactsUnderMbbSymbol()
    {
        await using var db = NewDb(nameof(Recompute_Daily_WritesAllArtifactsUnderMbbSymbol));
        var barCount = await SeedDailyCandlesAsync(db, 80, new DateOnly(2024, 1, 1));
        var svc = NewService(db);

        var report = await svc.RecomputeAsync("MBB", "1d");

        Assert.Equal("MBB", report.Symbol);
        Assert.Equal("1d", report.Timeframe);
        Assert.Equal(barCount, report.CandleCount);
        Assert.Equal(MbbRecomputeStatuses.Succeeded, report.Status);
        Assert.All(report.Modules,
            m => Assert.Equal(MbbRecomputeModuleStatuses.Recomputed, m.Status));

        var indicators = await db.TechnicalIndicators.ToListAsync();
        Assert.Equal(barCount, indicators.Count);
        Assert.All(indicators, x =>
        {
            Assert.Equal("MBB", x.Symbol);
            Assert.Equal("1d", x.Timeframe);
        });

        var volume = await db.CandleVolumeStats.ToListAsync();
        Assert.Equal(barCount, volume.Count);
        Assert.All(volume, x => Assert.Equal("MBB", x.Symbol));

        var patterns = await db.CandlePatterns.ToListAsync();
        Assert.True(patterns.Count >= 5, $"expected >=5 patterns, got {patterns.Count}");
        Assert.Contains(patterns, x => x.PatternType == "Doji");
        Assert.All(patterns, x => Assert.Equal("MBB", x.Symbol));

        var sequences = await db.PatternSequences.ToListAsync();
        Assert.True(sequences.Count > 0);
        Assert.All(sequences, x => Assert.Equal("MBB", x.Symbol));

        var vectors = await db.WindowVectors.ToListAsync();
        Assert.True(vectors.Count > 0);
        Assert.All(vectors, x =>
        {
            Assert.Equal("MBB", x.Symbol);
            Assert.Equal("1d", x.Timeframe);
        });
    }

    [Fact]
    public async Task Recompute_Hourly_SessionBreaksStayContiguousForIndicators()
    {
        await using var db = NewDb(nameof(Recompute_Hourly_SessionBreaksStayContiguousForIndicators));
        // 12 trading days x 5 hourly bars = 60 bars spanning lunch breaks, overnights
        // and two weekends. With a fixed-interval cadence every segment would be <=3
        // bars and RSI-14 could never warm up; session adjacency keeps it contiguous.
        var barCount = await SeedHourlyCandlesAsync(db, 12, new DateOnly(2024, 6, 3));
        Assert.Equal(60, barCount);
        var svc = NewService(db);

        var report = await svc.RecomputeAsync("MBB", "1h");

        Assert.Equal(MbbRecomputeStatuses.Succeeded, report.Status);
        var indicators = await db.TechnicalIndicators.ToListAsync();
        Assert.Equal(barCount, indicators.Count);
        Assert.All(indicators, x =>
        {
            Assert.Equal("MBB", x.Symbol);
            Assert.Equal("1h", x.Timeframe);
        });
        // Decisive: RSI-14 warms up only if lunch/overnight/weekend adjacency held.
        Assert.True(indicators.Count(x => x.Rsi14.HasValue) > 0,
            "RSI-14 stayed null: session breaks were treated as data gaps");
    }

    [Fact]
    public async Task Recompute_UsesInstrumentCandlesNotKlines_AndLeavesBtcArtifactsAlone()
    {
        await using var db = NewDb(nameof(Recompute_UsesInstrumentCandlesNotKlines_AndLeavesBtcArtifactsAlone));
        var barCount = await SeedDailyCandlesAsync(db, 80, new DateOnly(2024, 1, 1));

        // Decoys: a BTCUSDT Klines series (different count) plus a bogus "MBB" row in
        // the Binance table. If recompute read Klines it would see 30 + 1 bars, not 80.
        var klines = new List<Kline>();
        for (var i = 0; i < 30; i++)
        {
            klines.Add(new Kline
            {
                Symbol = "BTCUSDT", Timeframe = "1d",
                OpenTimeMs = 1_700_000_000_000L + i * 86_400_000L,
                CloseTimeMs = 1_700_000_000_000L + (i + 1) * 86_400_000L - 1,
                Open = 40_000m, High = 41_000m, Low = 39_000m, Close = 40_500m, Volume = 100m,
            });
        }
        klines.Add(new Kline
        {
            Symbol = "MBB", Timeframe = "1d",
            OpenTimeMs = 1_700_000_000_000L, CloseTimeMs = 1_700_086_399_999L,
            Open = 1m, High = 2m, Low = 0.5m, Close = 1.5m, Volume = 1m,
        });
        db.Klines.AddRange(klines);

        // Pre-existing BTC artifacts must survive the MBB rebuild untouched.
        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = "BTCUSDT", Timeframe = "1d", OpenTimeMs = 1_700_000_000_000L, Rsi14 = 55.5,
        });
        db.CandleVolumeStats.Add(new CandleVolumeStats
        {
            Symbol = "BTCUSDT", Timeframe = "1d", OpenTimeMs = 1_700_000_000_000L, Volume = 1m,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var report = await svc.RecomputeAsync("MBB", "1d");

        Assert.Equal(barCount, report.CandleCount);
        // Indicators were computed from the 80 InstrumentCandles, not the 31 Klines.
        Assert.Equal(barCount,
            await db.TechnicalIndicators.CountAsync(x => x.Symbol == "MBB"));

        var btcIndicators = await db.TechnicalIndicators
            .Where(x => x.Symbol == "BTCUSDT").ToListAsync();
        Assert.Single(btcIndicators);
        Assert.Equal(55.5, btcIndicators[0].Rsi14);
        Assert.Single(await db.CandleVolumeStats
            .Where(x => x.Symbol == "BTCUSDT").ToListAsync());

        // No artifact may be stamped BTCUSDT by the MBB run, and Klines stay untouched.
        Assert.Equal(31, await db.Klines.CountAsync());
        Assert.DoesNotContain(await db.WindowVectors.ToListAsync(), x => x.Symbol != "MBB");
        Assert.DoesNotContain(await db.CandlePatterns.ToListAsync(), x => x.Symbol != "MBB");
        Assert.DoesNotContain(await db.PatternSequences.ToListAsync(), x => x.Symbol != "MBB");
    }

    [Fact]
    public async Task Recompute_Rerun_IsIdempotent_NoDuplicates()
    {
        await using var db = NewDb(nameof(Recompute_Rerun_IsIdempotent_NoDuplicates));
        await SeedDailyCandlesAsync(db, 80, new DateOnly(2024, 1, 1));
        var svc = NewService(db);

        await svc.RecomputeAsync("MBB", "1d");
        var snapshot = new
        {
            Indicators = await db.TechnicalIndicators.CountAsync(),
            Patterns = await db.CandlePatterns.CountAsync(),
            Volume = await db.CandleVolumeStats.CountAsync(),
            Sequences = await db.PatternSequences.CountAsync(),
            Vectors = await db.WindowVectors.CountAsync(),
        };

        var second = await svc.RecomputeAsync("MBB", "1d");

        Assert.Equal(MbbRecomputeStatuses.Succeeded, second.Status);
        Assert.Equal(snapshot.Indicators, await db.TechnicalIndicators.CountAsync());
        Assert.Equal(snapshot.Patterns, await db.CandlePatterns.CountAsync());
        Assert.Equal(snapshot.Volume, await db.CandleVolumeStats.CountAsync());
        Assert.Equal(snapshot.Sequences, await db.PatternSequences.CountAsync());
        Assert.Equal(snapshot.Vectors, await db.WindowVectors.CountAsync());
    }

    [Fact]
    public async Task Recompute_FailsClosed_OnUnknownSymbol_BtcSymbol_And4h()
    {
        await using var db = NewDb(nameof(Recompute_FailsClosed_OnUnknownSymbol_BtcSymbol_And4h));
        var svc = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("XXX", "1d"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("BTCUSDT", "1h"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("MBB", "4h"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("MBB", "4H"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("MBB", "15m"));
    }

    [Fact]
    public async Task Recompute_NoCandles_ReportsNoDataWithoutWriting()
    {
        await using var db = NewDb(nameof(Recompute_NoCandles_ReportsNoDataWithoutWriting));
        var svc = NewService(db);

        var report = await svc.RecomputeAsync("MBB", "1d");

        Assert.Equal(MbbRecomputeStatuses.NoData, report.Status);
        Assert.Equal(0, report.CandleCount);
        Assert.All(report.Modules,
            m => Assert.Equal(MbbRecomputeModuleStatuses.Skipped, m.Status));
        Assert.Equal(0, await db.TechnicalIndicators.CountAsync());
        Assert.Equal(0, await db.CandleVolumeStats.CountAsync());
    }

    [Fact]
    public void SessionAdjacency_LunchOvernightWeekendContiguous_MissingBarBreaks()
    {
        var cal = new VietnamMarketCalendar();
        var mon = new DateOnly(2024, 6, 17); // Monday
        var bars = new List<OhlcvBar>();
        foreach (var d in cal.EnumerateTradingDates(mon, mon.AddDays(7))) // Mon..Fri
        {
            foreach (var open in VietnamMarketCalendar.HourlyOpensUtcMs(d))
                bars.Add(new OhlcvBar(open, open + 3_600_000L, 1m, 2m, 0.5m, 1.5m, 100m));
        }

        var adjacency = MbbTechnicalRecomputeService.BuildSessionAdjacency(cal, "1h", bars);

        // Lunch: 11:00 -> 13:00 same day is contiguous (no 12:00 expected slot).
        var d0 = mon;
        Assert.True(adjacency(
            VietnamMarketCalendar.ToUtcMs(d0, new TimeOnly(11, 0)),
            VietnamMarketCalendar.ToUtcMs(d0, new TimeOnly(13, 0))));
        // Overnight: 14:00 -> next-day 09:00 contiguous.
        var d1 = mon.AddDays(1);
        Assert.True(adjacency(
            VietnamMarketCalendar.ToUtcMs(d0, new TimeOnly(14, 0)),
            VietnamMarketCalendar.ToUtcMs(d1, new TimeOnly(9, 0))));
        // Weekend: Fri 14:00 -> Mon 09:00 contiguous.
        var fri = mon.AddDays(4);
        var nextMon = mon.AddDays(7);
        Assert.True(adjacency(
            VietnamMarketCalendar.ToUtcMs(fri, new TimeOnly(14, 0)),
            VietnamMarketCalendar.ToUtcMs(nextMon, new TimeOnly(9, 0))));
        // Missing expected bar: 11:00 -> 14:00 is NOT contiguous (13:00 bar missing).
        Assert.False(adjacency(
            VietnamMarketCalendar.ToUtcMs(d0, new TimeOnly(11, 0)),
            VietnamMarketCalendar.ToUtcMs(d0, new TimeOnly(14, 0))));
    }

    [Fact]
    public async Task Btc_KlineIndexer_StillWorks_AndIsUnaffectedByMbbArtifacts()
    {
        await using var db = NewDb(nameof(Btc_KlineIndexer_StillWorks_AndIsUnaffectedByMbbArtifacts));
        // BTC still indexes from Klines through the unchanged incremental path.
        var klines = Enumerable.Range(0, 60).Select(i => new Kline
        {
            Symbol = "BTCUSDT", Timeframe = "1h",
            OpenTimeMs = 1_700_000_000_000L + i * 3_600_000L,
            CloseTimeMs = 1_700_000_000_000L + (i + 1) * 3_600_000L - 1,
            Open = 40_000m + i, High = 40_100m + i, Low = 39_900m + i,
            Close = 40_050m + i, Volume = 10m + i % 5,
        }).ToArray();
        db.Klines.AddRange(klines);
        await db.SaveChangesAsync();

        var techIndexer = new TechnicalIndicatorIndexer(
            db, NullLogger<TechnicalIndicatorIndexer>.Instance,
            OptionsFactory.Create(TestIndexingOptions()));
        var written = await techIndexer.IndexAsync("BTCUSDT", "1h");
        Assert.Equal(60, written);

        // MBB recompute afterwards must not touch the BTCUSDT rows.
        await SeedDailyCandlesAsync(db, 60, new DateOnly(2024, 1, 1));
        var svc = NewService(db);
        await svc.RecomputeAsync("MBB", "1d");

        var btcRows = await db.TechnicalIndicators
            .Where(x => x.Symbol == "BTCUSDT").ToListAsync();
        Assert.Equal(60, btcRows.Count);
        Assert.Equal(60, await db.TechnicalIndicators.CountAsync(x => x.Symbol == "MBB"));
    }
}
