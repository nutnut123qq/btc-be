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
}
