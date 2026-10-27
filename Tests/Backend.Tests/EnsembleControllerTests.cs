using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public class EnsembleControllerTests
{
    [Fact]
    public async Task GetEnsembleHistory_LimitIsClamped()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        for (var i = 0; i < 205; i++)
            db.EnsemblePredictionRecords.Add(new EnsemblePredictionRecord { Symbol = "BTCUSDT", Timeframe = "4h", TimeMs = 1_000_000 + i });
        await db.SaveChangesAsync();
        var controller = new EnsembleController(new EnsembleService(db, new FakeBinanceKlinesService()));

        Assert.Equal(200, ItemCount(await controller.GetEnsembleHistory("BTCUSDT", "4h", limit: 500)));
        Assert.Equal(2, ItemCount(await controller.GetEnsembleHistory("BTCUSDT", "4h", limit: 2)));
        Assert.Equal(1, ItemCount(await controller.GetEnsembleHistory("BTCUSDT", "4h", limit: 0)));
        Assert.Equal(1, ItemCount(await controller.GetEnsembleHistory("BTCUSDT", "4h", limit: -5)));
    }

    [Fact]
    public void ParseLayers_AcceptsLegacyArray()
    {
        var layers = EnsembleController.ParseLayers("[{\"layerName\":\"legacy\"}]");

        Assert.Single(layers);
        Assert.Equal("legacy", layers[0].LayerName);
    }

    [Fact]
    public void ParseLayers_AcceptsCurrentEnvelope()
    {
        var layers = EnsembleController.ParseLayers("{\"isDegraded\":false,\"layers\":[{\"layerName\":\"current\",\"normalizedWeight\":0.45,\"probUp\":0.8,\"probDown\":0.1,\"probSideways\":0.1}]}");

        Assert.Single(layers);
        Assert.Equal("current", layers[0].LayerName);
        Assert.Equal(0.45, layers[0].Weight);
        Assert.Equal("Bullish", layers[0].Direction);
    }

    [Fact]
    public void ParseLayers_MalformedJsonFallsBackToEmpty()
    {
        Assert.Empty(EnsembleController.ParseLayers("not-json"));
    }

    private static int ItemCount(IActionResult result) =>
        Assert.IsAssignableFrom<IEnumerable<object>>(Assert.IsType<OkObjectResult>(result).Value).Count();
}
