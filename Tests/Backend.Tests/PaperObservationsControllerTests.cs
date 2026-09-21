using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

public sealed class PaperObservationsControllerTests
{
    [Fact]
    public async Task Get_returns_truthful_empty_state_when_recorder_table_is_unavailable()
    {
        var expected = new PaperObservationListResponse("BTCUSDT", false, "not initialized", []);
        var controller = new PaperObservationsController(new FakeReader(expected));

        var result = await controller.Get("BTCUSDT", 25, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task Reader_rejects_symbols_outside_the_BTC_only_scope_before_accessing_storage()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var reader = new PaperObservationReader(db, new ProductionSymbolPolicy());

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.GetLatestAsync("ETHUSDT", 25, CancellationToken.None));

        Assert.Contains("BTCUSDT only", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeReader(PaperObservationListResponse response) : IPaperObservationReader
    {
        public Task<PaperObservationListResponse> GetLatestAsync(string symbol, int take, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
