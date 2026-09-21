using Backend.Services;

namespace Backend.Tests;

public class ProductionSymbolPolicyTests
{
    private readonly ProductionSymbolPolicy _policy = new();

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData(" btcusdt ")]
    [InlineData("BTC")]
    public void EnsureActive_NormalizesBtcAliases(string input) =>
        Assert.Equal("BTCUSDT", _policy.EnsureActive(input));

    [Theory]
    [InlineData("ETHUSDT")]
    [InlineData("SOLUSDT")]
    [InlineData("")]
    public void EnsureActive_RejectsAnythingOutsideBtc(string input)
    {
        var error = Assert.Throws<ArgumentException>(() => _policy.EnsureActive(input));
        Assert.Contains("BTCUSDT only", error.Message);
    }

    [Fact]
    public void NormalizeListAndEnsureActive_RejectsMixedPortfolio() =>
        Assert.Throws<ArgumentException>(() => _policy.NormalizeListAndEnsureActive("BTCUSDT,ETHUSDT"));
}
