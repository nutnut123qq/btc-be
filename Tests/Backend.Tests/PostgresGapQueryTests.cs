using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Backend.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Backend.Options;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Backend.Tests;

public class PostgresGapQueryTests
{
    private readonly ITestOutputHelper _output;

    public PostgresGapQueryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PostgresConnection_IsRequiredInCi()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES")));
    }

    [Fact]
    public async Task LagQuery_ExecutesOnPostgresWithoutMaterializingKlines()
    {
        var connectionString = Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString).Options);
        var symbol = $"LAGTEST{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        const long start = 1_700_000_000_000;
        try
        {
            db.Klines.AddRange(
                CreateKline(symbol, start),
                CreateKline(symbol, start + 60_000),
                CreateKline(symbol, start + 180_000));
            await db.SaveChangesAsync();

            var rows = await KlineGapQuery.GetTopInternalGapsAsync(
                db, symbol, "1m", 60_000, 10, start, start + 180_000, default);

            var gap = Assert.Single(rows);
            Assert.Equal(1, gap.MissingBars);
            Assert.Equal(1, gap.GapRangeCount);
        }
        finally
        {
            await db.Klines.Where(row => row.Symbol == symbol).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Readiness_UnreachablePostgresReturnsWithinShortTimeout()
    {
        var connectionString = Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES_TIMEOUT");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString).Options);
        var controller = new HealthController(db, NullLogger<HealthController>.Instance);
        var stopwatch = Stopwatch.StartNew();

        var result = await controller.Ready(default);

        stopwatch.Stop();
        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DataAudit_ColdCompletesWithinPerformanceBudget()
    {
        var connectionString = Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES_PERFORMANCE");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<DataAuditCache>();
        services.Configure<KlinesIngestionOptions>(_ => { });
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IDataAuditService, DataAuditService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.CanConnectAsync());
        var service = scope.ServiceProvider.GetRequiredService<IDataAuditService>();
        var stopwatch = Stopwatch.StartNew();

        var result = await service.AuditAsync("BTCUSDT");

        stopwatch.Stop();
        _output.WriteLine($"Cold BTC data audit: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Equal(7, result.Timeframes.Count);
        Assert.All(result.Timeframes, timeframe => Assert.NotNull(timeframe.ExpectedBars));
        Assert.All(result.Timeframes, timeframe => Assert.Equal(GapLedgerStatuses.Reconciled, timeframe.GapLedgerStatus));
        Assert.All(result.Timeframes, timeframe => Assert.Null(timeframe.CandlePatterns));
        Assert.All(result.Timeframes.Where(x => x.MissingBars == 0), timeframe =>
        {
            Assert.Equal(0, timeframe.GapRangeCount);
            Assert.Empty(timeframe.TopGaps);
        });
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Cold audit took {stopwatch.Elapsed.TotalMilliseconds:F2} ms (budget: 3000 ms).");

        var coldDurations = new List<double> { stopwatch.Elapsed.TotalMilliseconds };
        var auditCache = scope.ServiceProvider.GetRequiredService<DataAuditCache>();
        for (var i = 1; i < 20; i++)
        {
            auditCache.Invalidate("BTCUSDT");
            stopwatch.Restart();
            await service.AuditAsync("BTCUSDT");
            stopwatch.Stop();
            coldDurations.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        coldDurations.Sort();
        var coldP95 = coldDurations[18];
        _output.WriteLine($"Cold BTC data audit p95 (20 runs): {coldP95:F2} ms");
        Assert.True(coldP95 < 3000, $"Cold p95 was {coldP95:F2} ms (budget: 3000 ms).");

        var cachedDurations = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            stopwatch.Restart();
            await service.AuditAsync("BTCUSDT");
            stopwatch.Stop();
            cachedDurations.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        cachedDurations.Sort();
        var cachedP95 = cachedDurations[18];
        _output.WriteLine($"Cached BTC data audit p95 (20 runs): {cachedP95:F2} ms");
        Assert.True(cachedP95 < 1000, $"Cached p95 was {cachedP95:F2} ms (budget: 1000 ms).");
    }

    [Fact]
    public async Task DataAudit_UnbootstrappedSymbolUsesTruthfulLiveFallback()
    {
        var connectionString = Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<DataAuditCache>();
        services.Configure<KlinesIngestionOptions>(_ => { });
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IDataAuditService, DataAuditService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDataAuditService>();

        var result = await service.AuditAsync("UNBOOTSTRAPPED_TEST_SYMBOL");

        Assert.All(result.Timeframes, timeframe =>
        {
            Assert.Equal(GapLedgerStatuses.LiveFallback, timeframe.GapLedgerStatus);
            Assert.Equal(0, timeframe.TotalKlines);
            Assert.True(timeframe.ExpectedBars > 0);
            Assert.Equal(timeframe.ExpectedBars, timeframe.MissingBars);
            Assert.Single(timeframe.TopGaps);
        });
    }

    [Fact]
    public async Task DataAudit_PartialSymbolLedgerDoesNotMarkEmptyTimeframeReconciled()
    {
        var connectionString = Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var symbol = $"AUDITPARTIAL{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<DataAuditCache>();
        services.Configure<KlinesIngestionOptions>(_ => { });
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IDataAuditService, DataAuditService>();
        await using var provider = services.BuildServiceProvider();

        try
        {
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.KlineGapStates.Add(new KlineGapState
                {
                    Symbol = symbol,
                    Timeframe = "1h",
                    StartOpenTimeMs = 1_700_000_000_000,
                    EndOpenTimeMs = 1_700_000_000_000,
                    MissingBars = 1,
                    Status = KlineGapStatuses.Pending,
                    Reason = "POSTGRES_PARTIAL_LEDGER_TEST",
                    FirstDetectedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            await using var auditScope = provider.CreateAsyncScope();
            var service = auditScope.ServiceProvider.GetRequiredService<IDataAuditService>();
            var result = await service.AuditAsync(symbol);

            var emptyOneMinute = Assert.Single(result.Timeframes, row => row.Timeframe == "1m");
            Assert.Equal(GapLedgerStatuses.LiveFallback, emptyOneMinute.GapLedgerStatus);
            Assert.Equal(0, emptyOneMinute.TotalKlines);
            Assert.Equal(emptyOneMinute.ExpectedBars, emptyOneMinute.MissingBars);
        }
        finally
        {
            await using var cleanupScope = provider.CreateAsyncScope();
            var db = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.KlineGapStates
                .Where(row => row.Symbol == symbol)
                .ExecuteDeleteAsync();
        }
    }

    private static Kline CreateKline(string symbol, long openTimeMs) => new()
    {
        Symbol = symbol,
        Timeframe = "1m",
        OpenTimeMs = openTimeMs,
        CloseTimeMs = openTimeMs + 59_999,
        Open = 1,
        High = 1,
        Low = 1,
        Close = 1,
        Volume = 1,
        QuoteVolume = 1,
        TradeCount = 1,
        TakerBuyVolume = 1,
        TakerBuyQuoteVolume = 1
    };
}
