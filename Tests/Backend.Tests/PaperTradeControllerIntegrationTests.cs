using System.Text.Json;

namespace Backend.Tests;

public class PaperTradeControllerIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public PaperTradeControllerIntegrationTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Open_ReturnsStableEnvelope()
    {
        var json = await GetJson("/api/paper-trades/open");

        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("count").ValueKind);
    }

    [Fact]
    public async Task EquityCurve_ReturnsStableEnvelope()
    {
        var json = await GetJson("/api/paper-trades/equity-curve");

        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("points").ValueKind);
    }

    [Fact]
    public async Task Summary_DefaultsToBtcAndReturnsAggregateShape()
    {
        var json = await GetJson("/api/paper-trades/summary");

        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("totalTrades").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("winRate").ValueKind);
    }

    [Theory]
    [InlineData("/api/paper-trades?symbols=all")]
    [InlineData("/api/paper-trades?symbols=BTCUSDT,ETHUSDT")]
    [InlineData("/api/paper-trades/summary?symbol=ETHUSDT")]
    [InlineData("/api/paper-trades/equity-curve?symbol=ETHUSDT")]
    [InlineData("/api/paper-trades/open?symbol=ETHUSDT")]
    public async Task ReadEndpoints_RejectSymbolsOutsideBtcScope(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNSUPPORTED_SYMBOL", json.RootElement.GetProperty("code").GetString());
    }

    private async Task<JsonDocument> GetJson(string path)
    {
        var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
