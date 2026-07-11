using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class KlinesBackfillServiceTests
{
    private static AppDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static IServiceScopeFactory CreateScopeFactory(string dbName, IBinanceKlinesService binance)
    {
        var services = new ServiceCollection();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        services.AddScoped<AppDbContext>(_ => new AppDbContext(dbOptions));
        services.AddSingleton(binance);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static KlinesBackfillService CreateService(string dbName, IBinanceKlinesService binance)
    {
        return new KlinesBackfillService(
            CreateScopeFactory(dbName, binance),
            new FakeHostLifetime(),
            NullLogger<KlinesBackfillService>.Instance);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ReturnsAlreadyRunning()
    {
        var dbName = Guid.NewGuid().ToString();
        var binance = new RangeFakeBinance(TimeSpan.FromMilliseconds(200));
        var service = CreateService(dbName, binance);

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);

        // Khởi động backfill không đợi để giữ _isRunning = 1 trong một khoảng ngắn.
        var first = await service.StartAsync("BTCUSDT", new[] { "1d" }, start, end, wait: false);
        Assert.Equal("accepted", first.Status);

        // Gọi lại ngay phải trả về already_running.
        var second = await service.StartAsync("BTCUSDT", new[] { "1d" }, start, end, wait: false);
        Assert.Equal("already_running", second.Status);

        // Đợi job đầu tiên hoàn thành để reset static flag cho các test khác.
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (service.IsRunning && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_BackfillsSmallTimeframe_InsertsRows()
    {
        var dbName = Guid.NewGuid().ToString();
        var binance = new RangeFakeBinance();
        var service = CreateService(dbName, binance);

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        var result = await service.StartAsync("BTCUSDT", new[] { "1d" }, start, end, wait: true);

        Assert.Equal("completed", result.Status);
        await using var db = CreateInMemoryDb(dbName);
        Assert.Equal(5, await db.Klines.CountAsync(k => k.Symbol == "BTCUSDT" && k.Timeframe == "1d"));
    }

    [Fact]
    public async Task StartAsync_RunTwiceOnSameData_SkipsDuplicates()
    {
        var dbName = Guid.NewGuid().ToString();
        var binance = new RangeFakeBinance();
        var service = CreateService(dbName, binance);

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        var first = await service.StartAsync("BTCUSDT", new[] { "1d" }, start, end, wait: true);
        Assert.Equal("completed", first.Status);

        await using var dbAfterFirst = CreateInMemoryDb(dbName);
        var countAfterFirst = await dbAfterFirst.Klines.CountAsync(k => k.Symbol == "BTCUSDT" && k.Timeframe == "1d");
        Assert.Equal(5, countAfterFirst);

        var second = await service.StartAsync("BTCUSDT", new[] { "1d" }, start, end, wait: true);
        Assert.Equal("completed", second.Status);

        await using var dbAfterSecond = CreateInMemoryDb(dbName);
        var countAfterSecond = await dbAfterSecond.Klines.CountAsync(k => k.Symbol == "BTCUSDT" && k.Timeframe == "1d");
        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
    public async Task StartAsync_ResumeFromExistingData_ContinuesFromLatestPlusInterval()
    {
        var dbName = Guid.NewGuid().ToString();
        var binance = new RangeFakeBinance();
        var service = CreateService(dbName, binance);

        // Seed sẵn 2 nến 1d.
        await using (var seedDb = CreateInMemoryDb(dbName))
        {
            seedDb.Klines.AddRange(
                CreateKline("1d", 1_704_067_200_000L, 86_400_000L), // 2024-01-01
                CreateKline("1d", 1_704_153_600_000L, 86_400_000L)); // 2024-01-02
            await seedDb.SaveChangesAsync();
        }

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        var result = await service.StartAsync("BTCUSDT", new[] { "1d" }, start, end, wait: true);
        Assert.Equal("completed", result.Status);

        await using var db = CreateInMemoryDb(dbName);
        var times = await db.Klines
            .Where(k => k.Symbol == "BTCUSDT" && k.Timeframe == "1d")
            .OrderBy(k => k.OpenTimeMs)
            .Select(k => k.OpenTimeMs)
            .ToListAsync();

        // Phải có đủ 5 nến từ 01 -> 05, không duplicate.
        Assert.Equal(5, times.Count);
        Assert.Equal(new[]
        {
            1_704_067_200_000L,
            1_704_153_600_000L,
            1_704_240_000_000L,
            1_704_326_400_000L,
            1_704_412_800_000L
        }, times);
    }

    private static Kline CreateKline(string timeframe, long openTimeMs, long intervalMs) => new()
    {
        Symbol = "BTCUSDT",
        Timeframe = timeframe,
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + intervalMs - 1,
        Open = 64000m,
        High = 64100m,
        Low = 63900m,
        Close = 64050m,
        Volume = 1m,
        QuoteVolume = 1m,
        TradeCount = 1,
        TakerBuyVolume = 0.5m,
        TakerBuyQuoteVolume = 0.5m
    };

    private class FakeHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>
    /// Fake Binance trả về các nến nằm trong [startTimeMs, endTimeMs], tối đa limit nến,
    /// theo đúng interval được yêu cầu. Có thể thêm delay để test concurrency.
    /// </summary>
    private class RangeFakeBinance : IBinanceKlinesService
    {
        private readonly TimeSpan? _delay;

        public RangeFakeBinance(TimeSpan? delay = null)
        {
            _delay = delay;
        }

        public async Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
            string symbol = "BTCUSDT",
            string interval = "1h",
            int limit = 48,
            long? startTimeMs = null,
            long? endTimeMs = null,
            CancellationToken cancellationToken = default)
        {
            if (_delay.HasValue)
            {
                await Task.Delay(_delay.Value, cancellationToken);
            }

            var intervalMs = Timeframes.IntervalToMs(interval);
            if (intervalMs <= 0) intervalMs = 3_600_000L;

            var start = startTimeMs ?? 0L;
            var end = endTimeMs ?? (start + (limit - 1) * intervalMs);

            var list = new List<KlineDto>();
            for (var t = start; t <= end && list.Count < limit; t += intervalMs)
            {
                list.Add(new KlineDto
                {
                    OpenTimeMs = t,
                    CloseTimeMs = t + intervalMs - 1,
                    Open = 64000m,
                    High = 64100m,
                    Low = 63900m,
                    Close = 64050m,
                    Volume = 1m,
                    QuoteVolume = 1m,
                    TradeCount = 1,
                    TakerBuyVolume = 0.5m,
                    TakerBuyQuoteVolume = 0.5m
                });
            }

            return list;
        }

        public Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(
            string interval = "1h", int limit = 48, CancellationToken cancellationToken = default)
            => GetKlinesAsync("BTCUSDT", interval, limit, cancellationToken: cancellationToken);

        public Task<string> BuildTechSummaryAsync(
            string interval = "1h", int limit = 48, CancellationToken cancellationToken = default)
            => Task.FromResult("fake summary");
    }
}
