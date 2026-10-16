using Backend.Options;
using Backend.Services;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

public class ProductionTimeframePolicyTests
{
    [Fact]
    public void Constructor_TrimsAndDeduplicatesConfiguredActiveTimeframes()
    {
        var options = OptionsFactory.Create(new ProductionTimeframeOptions
        {
            Active = [" 1h ", "4h", "1D", "1h"],
            Default = "4h"
        });

        var policy = new ProductionTimeframePolicy(options);

        Assert.Equal(["1h", "4h", "1d"], policy.Active);
        Assert.True(policy.IsActive("1d"));
        Assert.Equal("4h", policy.Default);
        Assert.Equal("4h", policy.EnsureActive(" 4H "));
    }

    [Fact]
    public void Constructor_RejectsMinuteProductionSet()
    {
        var options = OptionsFactory.Create(new ProductionTimeframeOptions
        {
            Active = ["1h", "4h", "1d", "15m"],
            Default = "4h"
        });

        Assert.Throws<InvalidOperationException>(() => { _ = new ProductionTimeframePolicy(options); });
    }

    [Fact]
    public void Constructor_RejectsNonFourHourDefaultEvenWhenItIsActive()
    {
        var options = OptionsFactory.Create(new ProductionTimeframeOptions
        {
            Active = ["1h", "4h", "1d"],
            Default = "1h"
        });

        var error = Assert.Throws<InvalidOperationException>(() => new ProductionTimeframePolicy(options));
        Assert.Contains("exactly 4h", error.Message);
    }

    [Fact]
    public void SymbolAwareQueries_KeepBtcScopeUnchanged()
    {
        var policy = new ProductionTimeframePolicy();

        Assert.Equal(["1h", "4h", "1d"], policy.ActiveFor("BTCUSDT"));
        Assert.Equal("4h", policy.DefaultFor("BTC"));
        Assert.True(policy.IsActive("BTCUSDT", "4h"));
        Assert.Equal("4h", policy.EnsureActive(" btc ", " 4H "));
        Assert.Equal(["1d", "1h", "4h"], policy.NormalizeAndEnsureActive("BTCUSDT", ["1d", "1h", "4h"]));
    }

    [Fact]
    public void SymbolAwareQueries_ApplyMbbContract()
    {
        var policy = new ProductionTimeframePolicy();

        Assert.Equal(["1h", "1d"], policy.ActiveFor("MBB"));
        Assert.Equal("1d", policy.DefaultFor("MBB"));
        Assert.True(policy.IsActive("MBB", "1h"));
        Assert.True(policy.IsActive(" mbb ", "1d"));
        Assert.False(policy.IsActive("MBB", "4h"));
        Assert.Equal("1d", policy.EnsureActive("MBB", " 1D "));
        Assert.Equal("1h", policy.EnsureActive("MBB", "1h"));
        Assert.Throws<ArgumentException>(() => policy.EnsureActive("MBB", "4h"));
        Assert.Throws<ArgumentException>(() => policy.EnsureActive("MBB", "15m"));
    }

    [Fact]
    public void SymbolAwareQueries_FailClosedOnUnknownSymbols()
    {
        var policy = new ProductionTimeframePolicy();

        Assert.False(policy.IsActive("ETHUSDT", "1d"));
        Assert.Throws<ArgumentException>(() => policy.ActiveFor("ETHUSDT"));
        Assert.Throws<ArgumentException>(() => policy.EnsureActive("DOGEUSDT", "1d"));
        Assert.Throws<ArgumentException>(() => policy.NormalizeAndEnsureActive("DOGEUSDT", ["1d"]));
    }
}
