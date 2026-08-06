using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class MlDatasetRebuildServiceTests
{
    private static AppDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static MlDatasetRebuildService CreateService(
        AppDbContext db,
        IMlDatasetService? mlDatasetService = null,
        IWindowDatasetService? windowDatasetService = null)
    {
        return new MlDatasetRebuildService(
            db,
            mlDatasetService ?? new FakeMlDatasetService(db),
            windowDatasetService ?? new FakeWindowDatasetService(db),
            NullLogger<MlDatasetRebuildService>.Instance);
    }

    [Fact]
    public async Task RebuildAsync_SingleTimeframe_PopulatesAllDatasets()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);
        var service = CreateService(db);

        var result = await service.RebuildAsync("BTCUSDT", new[] { "1h" }, MlDatasetRebuildService.DefaultHorizons);

        Assert.Equal("ok", result.PerTimeframe["1h"].Status);
        Assert.True(result.TotalMlFeatureStores > 0, "Expected MlFeatureStores > 0");
        Assert.True(result.TotalPriceTargets > 0, "Expected PriceTargets > 0");
        Assert.True(result.TotalWindowClassificationDatasets > 0, "Expected WindowClassificationDatasets > 0");

        Assert.True(await db.MlFeatureStores.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.PriceTargets.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.WindowClassificationDatasets.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
    }

    [Fact]
    public async Task RebuildAsync_DeleteOldData_KeepsOnlyNewData()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);

        // Seed dữ liệu cũ.
        db.MlFeatureStores.Add(new MlFeatureStore
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 999_999_999_999L,
            NullRatio = 0
        });
        db.PriceTargets.Add(new PriceTarget
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            OpenTimeMs = 999_999_999_999L,
            TargetReturn1h = 0.01
        });
        db.WindowClassificationDatasets.Add(new WindowClassificationDataset
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            WindowSize = 10,
            Horizon = "1h",
            WindowStartMs = 999_999_999_999L,
            WindowEndMs = 999_999_999_999L,
            FeatureVector = new[] { 0.1f },
            FeatureDim = 1,
            Label = 1,
            TargetReturn = 0.01,
            WindowNullRatio = 0
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.RebuildAsync("BTCUSDT", new[] { "1h" }, MlDatasetRebuildService.DefaultHorizons);

        Assert.Equal("ok", result.PerTimeframe["1h"].Status);

        // Dữ liệu cũ phải bị xóa.
        Assert.False(await db.MlFeatureStores.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.OpenTimeMs == 999_999_999_999L));
        Assert.False(await db.PriceTargets.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.OpenTimeMs == 999_999_999_999L));
        Assert.False(await db.WindowClassificationDatasets.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h" && x.WindowStartMs == 999_999_999_999L));

        // Dữ liệu mới phải tồn tại.
        Assert.True(await db.MlFeatureStores.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.PriceTargets.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
        Assert.True(await db.WindowClassificationDatasets.AnyAsync(x => x.Symbol == "BTCUSDT" && x.Timeframe == "1h"));
    }

    [Fact]
    public async Task RebuildAsync_PerHorizonBreakdown_IsCorrect()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = CreateInMemoryDb(dbName);
        var service = CreateService(db);

        var result = await service.RebuildAsync("BTCUSDT", new[] { "1h" }, MlDatasetRebuildService.DefaultHorizons);

        var tfResult = result.PerTimeframe["1h"];
        Assert.Equal("ok", tfResult.Status);
        Assert.True(tfResult.WindowClassificationDatasetsByHorizon.ContainsKey("1h"));
        Assert.True(tfResult.WindowClassificationDatasetsByHorizon.ContainsKey("4h"));
        Assert.True(tfResult.WindowClassificationDatasetsByHorizon.ContainsKey("1d"));
        Assert.Equal(tfResult.WindowClassificationDatasetsTotal,
            tfResult.WindowClassificationDatasetsByHorizon.Values.Sum());
    }

    /// <summary>
    /// Fake ML dataset service: tạo 10 feature rows và 10 target rows mỗi lần BuildAsync được gọi.
    /// </summary>
    private class FakeMlDatasetService : IMlDatasetService
    {
        private readonly AppDbContext _db;

        public FakeMlDatasetService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<int> BuildAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            var baseTime = 1_000_000L;
            for (int i = 0; i < 10; i++)
            {
                _db.MlFeatureStores.Add(new MlFeatureStore
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    OpenTimeMs = baseTime + i * 3_600_000L,
                    NullRatio = 0.1
                });
                _db.PriceTargets.Add(new PriceTarget
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    OpenTimeMs = baseTime + i * 3_600_000L,
                    TargetReturn1h = 0.01 * (i % 2 == 0 ? 1 : -1)
                });
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            return 10;
        }
    }

    /// <summary>
    /// Fake window dataset service: tạo 5 window samples cho mỗi horizon.
    /// </summary>
    private class FakeWindowDatasetService : IWindowDatasetService
    {
        private readonly AppDbContext _db;

        public FakeWindowDatasetService(AppDbContext db)
        {
            _db = db;
        }

        public Task<int> BuildAsync(string symbol, string timeframe, int windowSize, string horizon, int? maxSamples = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<int> BuildAllAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<(float[] Vector, long WindowStartMs, long WindowEndMs)?> BuildLatestFeatureVectorAsync(string symbol, string timeframe, int windowSize, CancellationToken ct = default)
        {
            return Task.FromResult<(float[] Vector, long WindowStartMs, long WindowEndMs)?>(null);
        }

        public async Task<int> BuildHorizonAsync(string symbol, string timeframe, string horizon, int? maxSamplesPerWindowSize = null, CancellationToken ct = default)
        {
            var baseTime = 2_000_000L;
            for (int i = 0; i < 5; i++)
            {
                _db.WindowClassificationDatasets.Add(new WindowClassificationDataset
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    WindowSize = 10,
                    Horizon = horizon,
                    WindowStartMs = baseTime + i * 3_600_000L,
                    WindowEndMs = baseTime + (i + 9) * 3_600_000L,
                    FeatureVector = new[] { 0.1f, 0.2f, 0.3f },
                    FeatureDim = 3,
                    Label = 1,
                    TargetReturn = 0.01,
                    WindowNullRatio = 0.1
                });
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            return 5;
        }
    }
}
