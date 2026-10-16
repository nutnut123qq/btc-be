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
    public void EnsureActive_RejectsUnknownSymbols(string input)
    {
        var error = Assert.Throws<ArgumentException>(() => _policy.EnsureActive(input));
        Assert.Contains("BTCUSDT only", error.Message);
    }

    [Theory]
    [InlineData("MBB")]
    [InlineData(" mbb ")]
    public void EnsureActive_RejectsKnownButNotEnabledInstruments(string input)
    {
        var error = Assert.Throws<ArgumentException>(() => _policy.EnsureActive(input));
        Assert.Contains("not enabled", error.Message);
        Assert.Contains("MBB", error.Message);
    }

    [Fact]
    public void CatalogDistinguishesKnownFromEnabled()
    {
        Assert.True(_policy.IsKnown("MBB"));
        Assert.True(_policy.IsKnown("BTC")); // alias
        Assert.False(_policy.IsKnown("ETHUSDT"));

        Assert.False(_policy.IsActive("MBB"));
        Assert.True(_policy.IsActive("BTC"));

        var mbb = _policy.GetInstrument("mbb");
        Assert.NotNull(mbb);
        Assert.Equal("MBB", mbb.Symbol);
        Assert.Equal("HOSE", mbb.Venue);
        Assert.Equal("VND", mbb.Currency);
        Assert.Equal("Asia/Ho_Chi_Minh", mbb.Timezone);
        Assert.Equal("VCI", mbb.DataProvider);
        Assert.Equal("adjusted", mbb.PriceBasis);
        Assert.Null(_policy.GetInstrument("DOGEUSDT"));
    }

    [Fact]
    public void NormalizeListAndEnsureActive_RejectsMixedPortfolio() =>
        Assert.Throws<ArgumentException>(() => _policy.NormalizeListAndEnsureActive("BTCUSDT,ETHUSDT"));

    [Fact]
    public void NormalizeListAndEnsureActive_RejectsNotEnabledInstrument() =>
        Assert.Throws<ArgumentException>(() => _policy.NormalizeListAndEnsureActive("BTCUSDT,MBB"));
}
