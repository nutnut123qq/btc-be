using Backend.Data;
using Backend.Controllers;
using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Backend.Tests;

public sealed class KlineDataQualityServiceTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeMilliseconds(36_000_000);
    private const long Hour = 3_600_000;

    [Fact]
    public async Task Issues_expose_stable_gap_and_invalid_duration_taxonomy_with_lineage()
    {
        await using var db = CreateDb();
        db.KlineGapStates.Add(new KlineGapState
        {
            Symbol = "BTCUSDT", Timeframe = "1h", StartOpenTimeMs = Hour,
            EndOpenTimeMs = 2 * Hour, MissingBars = 2, Status = KlineGapStatuses.Pending,
            AttemptCount = 1, LastAttemptAtUtc = FixedNow.AddHours(-2).UtcDateTime,
            NextRetryAtUtc = FixedNow.AddHours(22).UtcDateTime, Reason = "Binance returned no data; retry deferred.",
            FirstDetectedAtUtc = FixedNow.AddDays(-1).UtcDateTime, UpdatedAtUtc = FixedNow.UtcDateTime
        });
        var malformed = Candle(3 * Hour);
        malformed.CloseTimeMs++;
        db.Klines.AddRange(Candle(0), malformed);
        await db.SaveChangesAsync();

        var response = await CreateService(db, new FakeBinance([])).GetIssuesAsync(
            "BTCUSDT", "1h", 0, 3 * Hour, 100);

        Assert.Equal(KlineDataQualityTaxonomy.Version, response.TaxonomyVersion);
        Assert.Equal(2, response.TotalKnownIssues);
        var gap = response.Issues.Single(x => x.IssueType == "missing_interval");
        Assert.Equal("upstream_no_data_retry", gap.CauseCode);
        Assert.Equal("retry_scheduled", gap.ResolutionState);
        Assert.Equal("binance_spot_empty", gap.Evidence.SourceClassification);
        Assert.Equal(2, gap.AffectedBars);
        var duration = response.Issues.Single(x => x.IssueType == "invalid_duration");
        Assert.Equal(Hour, duration.ExpectedDurationMs);
        Assert.Equal(Hour + 1, duration.ActualDurationMs);
        Assert.Equal("not_checked", duration.Evidence.SourceClassification);
        Assert.Contains("TechnicalIndicators", duration.AffectedDownstreamArtifacts);
    }

    [Fact]
    public async Task Dry_run_is_read_only_and_never_synthesizes_missing_source_rows()
    {
        await using var db = CreateDb();
        var source = CandleDto(0);
        var service = CreateService(db, new FakeBinance([source]));

        var result = await service.RepairAsync(new KlineDataRepairRequest
        {
            Symbol = "BTCUSDT", Timeframe = "1h", IssueType = "missing_interval",
            StartOpenTimeMs = 0, EndOpenTimeMs = Hour, DryRun = true
        });

        Assert.False(result.Applied);
        Assert.Equal(1, result.InsertedBars);
        Assert.Equal([Hour], result.UnresolvedOpenTimeMs);
        Assert.Equal(0, await db.Klines.CountAsync());
        Assert.Equal(0, await db.KlineDataRepairRuns.CountAsync());
        Assert.Equal(64, result.PlanSha256.Length);
        Assert.Equal("binance_spot_verified", result.SourceClassification);
    }

    [Fact]
    public async Task Apply_requires_exact_preview_repairs_source_rows_and_is_idempotent()
    {
        await using var db = CreateDb();
        var malformed = Candle(Hour);
        malformed.CloseTimeMs += 11;
        db.Klines.Add(malformed);
        await db.SaveChangesAsync();
        var fake = new FakeBinance([CandleDto(Hour)]);
        var service = CreateService(db, fake);
        var request = new KlineDataRepairRequest
        {
            Symbol = "BTCUSDT", Timeframe = "1h", IssueType = "invalid_duration",
            StartOpenTimeMs = Hour, EndOpenTimeMs = Hour, DryRun = true
        };
        var preview = await service.RepairAsync(request);
        request.DryRun = false;
        request.ExpectedPlanSha256 = preview.PlanSha256;

        var applied = await service.RepairAsync(request);
        db.ChangeTracker.Clear();
        var repeated = await service.RepairAsync(request);

        Assert.True(applied.Applied);
        Assert.Equal(1, applied.ReplacedBars);
        Assert.Equal(2 * Hour - 1, (await db.Klines.SingleAsync()).CloseTimeMs);
        Assert.Single(await db.KlineDataRepairRuns.ToListAsync());
        Assert.True(repeated.AlreadyApplied);
        Assert.Equal(applied.ReplacedBars, repeated.ReplacedBars);
        Assert.Equal(applied.UnresolvedOpenTimeMs, repeated.UnresolvedOpenTimeMs);
        Assert.True(repeated.DerivedRebuildRequired);
        Assert.Single(await db.KlineDataRepairRuns.ToListAsync());
    }

    [Fact]
    public async Task Apply_fails_closed_when_source_or_database_drift_from_preview()
    {
        await using var db = CreateDb();
        var fake = new FakeBinance([CandleDto(0)]);
        var service = CreateService(db, fake);
        var request = new KlineDataRepairRequest
        {
            IssueType = "missing_interval", Timeframe = "1h", StartOpenTimeMs = 0,
            EndOpenTimeMs = 0, DryRun = true
        };
        var preview = await service.RepairAsync(request);
        fake.Rows = [CandleDto(0, close: 9m)];
        request.DryRun = false;
        request.ExpectedPlanSha256 = preview.PlanSha256;

        var error = await Assert.ThrowsAsync<KlineDataQualityException>(() => service.RepairAsync(request));

        Assert.Equal("PREVIEW_DRIFT", error.Code);
        Assert.Empty(db.Klines);
    }

    [Fact]
    public async Task Repair_rejects_more_than_one_thousand_bars_before_source_call()
    {
        await using var db = CreateDb();
        var fake = new FakeBinance([]);
        var service = CreateService(db, fake);

        var error = await Assert.ThrowsAsync<KlineDataQualityException>(() => service.RepairAsync(new KlineDataRepairRequest
        {
            IssueType = "missing_interval", Timeframe = "1h", StartOpenTimeMs = 0,
            EndOpenTimeMs = 1_000 * Hour
        }));

        Assert.Equal("REPAIR_LIMIT_EXCEEDED", error.Code);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Apply_rejects_source_row_with_wrong_duration_without_mutation()
    {
        await using var db = CreateDb();
        var malformedSource = CandleDto(0);
        malformedSource.CloseTimeMs++;
        var service = CreateService(db, new FakeBinance([malformedSource]));
        var preview = await service.RepairAsync(new KlineDataRepairRequest
        {
            IssueType = "missing_interval", Timeframe = "1h", StartOpenTimeMs = 0,
            EndOpenTimeMs = 0, DryRun = true
        });

        Assert.Equal("binance_spot_rejected", preview.SourceClassification);
        Assert.Equal([0L], preview.UnresolvedOpenTimeMs);
        Assert.Equal(0, preview.InsertedBars);
        Assert.Empty(db.Klines);

        var applyError = await Assert.ThrowsAsync<KlineDataQualityException>(() => service.RepairAsync(new KlineDataRepairRequest
        {
            IssueType = "missing_interval", Timeframe = "1h", StartOpenTimeMs = 0,
            EndOpenTimeMs = 0, DryRun = false, ExpectedPlanSha256 = preview.PlanSha256
        }));
        Assert.Equal("SOURCE_NOT_VERIFIED", applyError.Code);
        Assert.Empty(db.KlineDataRepairRuns);
    }

    [Fact]
    public void Physical_gap_scan_reports_every_range_not_only_top_ranked_ledger_rows()
    {
        var opens = new[] { 0L, 2 * Hour, 4 * Hour, 6 * Hour };

        var gaps = KlineDataQualityService.DiscoverPhysicalGaps(opens, 0, 7 * Hour, Hour);

        Assert.Equal(4, gaps.Count);
        Assert.Equal(new[] { Hour, 3 * Hour, 5 * Hour, 7 * Hour }, gaps.Select(x => x.StartOpenTimeMs));
        Assert.All(gaps, x => Assert.Equal(1, x.MissingBars));
    }

    [Fact]
    public void Repair_contract_defaults_to_dry_run_and_endpoint_is_admin_guarded()
    {
        Assert.True(new KlineDataRepairRequest().DryRun);
        var action = typeof(DataQualityController).GetMethod(nameof(DataQualityController.Repair));
        Assert.NotNull(action);
        Assert.Single(action!.GetCustomAttributes(typeof(AdminGuardAttribute), inherit: true));
    }

    [Fact]
    public async Task Concurrent_identical_applies_share_one_receipt_and_second_resolves_idempotently()
    {
        var database = Guid.NewGuid().ToString();
        await using var firstDb = CreateDb(database);
        await using var secondDb = CreateDb(database);
        var source = new FakeBinance([CandleDto(0)]);
        var first = CreateService(firstDb, source);
        var second = CreateService(secondDb, source);
        var previewRequest = new KlineDataRepairRequest
        {
            IssueType = "missing_interval", Timeframe = "1h", StartOpenTimeMs = 0,
            EndOpenTimeMs = 0, DryRun = true
        };
        var preview = await first.RepairAsync(previewRequest);
        KlineDataRepairRequest ApplyRequest() => new()
        {
            IssueType = "missing_interval", Timeframe = "1h", StartOpenTimeMs = 0,
            EndOpenTimeMs = 0, DryRun = false, ExpectedPlanSha256 = preview.PlanSha256
        };

        var results = await Task.WhenAll(first.RepairAsync(ApplyRequest()), second.RepairAsync(ApplyRequest()));

        firstDb.ChangeTracker.Clear();
        Assert.Single(await firstDb.Klines.ToListAsync());
        Assert.Single(await firstDb.KlineDataRepairRuns.ToListAsync());
        Assert.Single(results.Where(x => x.AlreadyApplied));
        Assert.Single(results.Where(x => !x.AlreadyApplied && x.InsertedBars == 1));
    }

    private static KlineDataQualityService CreateService(AppDbContext db, IBinanceKlinesService source) => new(
        db, source, new DataAuditCache(new MemoryCache(new MemoryCacheOptions())),
        new FixedTimeProvider(FixedNow), new ProductionSymbolPolicy(), new ProductionTimeframePolicy());

    private static AppDbContext CreateDb(string? name = null) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private static Kline Candle(long open) => new()
    {
        Symbol = "BTCUSDT", Timeframe = "1h", OpenTimeMs = open, CloseTimeMs = open + Hour - 1,
        Open = 10, High = 12, Low = 9, Close = 11, Volume = 2, QuoteVolume = 20,
        TradeCount = 2, TakerBuyVolume = 1, TakerBuyQuoteVolume = 10
    };

    private static KlineDto CandleDto(long open, decimal close = 11m) => new()
    {
        OpenTimeMs = open, CloseTimeMs = open + Hour - 1, Open = 10, High = Math.Max(12, close),
        Low = 9, Close = close, Volume = 2, QuoteVolume = 20, TradeCount = 2,
        TakerBuyVolume = 1, TakerBuyQuoteVolume = 10
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeBinance(IReadOnlyList<KlineDto> rows) : IBinanceKlinesService
    {
        public IReadOnlyList<KlineDto> Rows { get; set; } = rows;
        public int Calls { get; private set; }
        public Task<IReadOnlyList<KlineDto>> GetKlinesAsync(string symbol = "BTCUSDT", string interval = "4h",
            int limit = 48, long? startTimeMs = null, long? endTimeMs = null,
            CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Rows); }
        public Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(string interval = "4h", int limit = 48, CancellationToken cancellationToken = default) => Task.FromResult(Rows);
        public Task<string> BuildTechSummaryAsync(string symbol = "BTCUSDT", string interval = "4h", int limit = 48, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<IReadOnlyList<MarketTickerDto>> Get24hTickersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketTickerDto>>([]);
        public Task<IReadOnlyList<MarketTradeDto>> GetRecentTradesAsync(string symbol = "BTCUSDT", int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketTradeDto>>([]);
        public Task<OrderBookDepthDto> GetOrderBookDepthAsync(string symbol = "BTCUSDT", int limit = 20, CancellationToken cancellationToken = default) => Task.FromResult(new OrderBookDepthDto());
    }
}
