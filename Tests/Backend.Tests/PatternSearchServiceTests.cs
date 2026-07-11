using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public class PatternSearchServiceTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public PatternSearchServiceTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SearchAsync_EmptyVectorStore_BuildsIndexAndReturnsResults()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPatternSearchService>();

        db.WindowVectors.RemoveRange(db.WindowVectors);
        await db.SaveChangesAsync();

        var request = new PatternSearchRequest
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            FeatureType = "close",
            WindowSize = 5,
            TopK = 3,
            LookbackBars = 200
        };

        // Act
        var response = await service.SearchAsync(request, requestId: "test-1");

        // Assert
        Assert.True(response.ScannedWindows > 0, "expected windows to be scanned");
        Assert.True(response.Items.Count > 0, "expected at least one similar window");
        Assert.True(response.Items.Count <= request.TopK, "items should not exceed TopK");
        Assert.All(response.Items, item =>
        {
            Assert.True(item.Similarity >= -1.0 && item.Similarity <= 1.0000001, "cosine similarity must be in [-1, 1] within floating-point tolerance");
            Assert.True(item.Distance >= -1e-6 && item.Distance <= 2.0, "distance = 1 - similarity within floating-point tolerance");
        });
    }

    [Fact]
    public async Task SearchAsync_MinGapBars_FiltersCloseWindows()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPatternSearchService>();

        db.WindowVectors.RemoveRange(db.WindowVectors);
        await db.SaveChangesAsync();

        var request = new PatternSearchRequest
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            FeatureType = "close",
            WindowSize = 5,
            TopK = 10,
            MinGapBars = 20,
            LookbackBars = 200
        };

        // Act
        var response = await service.SearchAsync(request, requestId: "test-2");

        // Assert: each selected window must be at least MinGapBars apart by start index
        var items = response.Items.OrderBy(i => i.StartTimeMs).ToList();
        for (int i = 1; i < items.Count; i++)
        {
            var gapBars = (items[i].StartTimeMs - items[i - 1].StartTimeMs) / 3_600_000L;
            Assert.True(gapBars >= request.MinGapBars,
                $"windows must respect MinGapBars: gap={gapBars} < {request.MinGapBars}");
        }
    }

    [Fact]
    public async Task SearchAsync_TooFewKlines_ReturnsEmptyItems()
    {
        // Arrange: request window size larger than available data from FakeBinanceKlinesService
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPatternSearchService>();

        var request = new PatternSearchRequest
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            FeatureType = "close",
            WindowSize = 60,
            TopK = 5,
            LookbackBars = 30
        };

        // Act
        var response = await service.SearchAsync(request, requestId: "test-3");

        // Assert
        Assert.Empty(response.Items);
        Assert.Equal(0, response.ScannedWindows);
    }
}
