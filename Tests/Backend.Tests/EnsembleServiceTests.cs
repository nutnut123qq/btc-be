using Backend.Services;
using Xunit;

namespace Backend.Tests;

public class EnsembleServiceTests
{
    [Fact]
    public void AggregateLayers_AllLayersActive_NormalizesWeightsAndAggregatesCorrectly()
    {
        var layers = new[]
        {
            new EnsembleService.EnsembleLayerInput("Confluence", 0.40, "Bullish", 0.80, 0.10, 0.10, "Summary 1"),
            new EnsembleService.EnsembleLayerInput("Markov", 0.30, "Bullish", 0.70, 0.20, 0.10, "Summary 2"),
            new EnsembleService.EnsembleLayerInput("Regime", 0.20, "Bullish", 0.90, 0.05, 0.05, "Summary 3"),
            new EnsembleService.EnsembleLayerInput("Smc", 0.10, "Bearish", 0.20, 0.70, 0.10, "Summary 4")
        };

        var (probUp, probDown, probSideways, direction, confidence, degraded, _) = EnsembleService.AggregateLayers(layers);

        Assert.Empty(degraded);
        Assert.Equal("Bullish", direction);
        Assert.True(probUp > 0.70);
        Assert.True(confidence > 0.70);
        Assert.Equal(1.0, Math.Round(probUp + probDown + probSideways, 2));
    }

    [Fact]
    public void AggregateLayers_WithDegradedLayers_ReNormalizesRemainingWeightsAndRecordsDegradedMetadata()
    {
        var layers = new[]
        {
            new EnsembleService.EnsembleLayerInput("Confluence", 0.50, "Bullish", 0.80, 0.10, 0.10, "Summary 1"),
            new EnsembleService.EnsembleLayerInput("Markov", 0.30, null, null, null, null, null, IsAvailable: false), // Degraded
            new EnsembleService.EnsembleLayerInput("Sentiment", 0.20, null, null, null, null, null, IsAvailable: false) // Degraded
        };

        var (probUp, probDown, probSideways, direction, confidence, degraded, _) = EnsembleService.AggregateLayers(layers);

        Assert.Equal(2, degraded.Count);
        Assert.Contains("Markov", degraded);
        Assert.Contains("Sentiment", degraded);
        Assert.Equal("Bullish", direction);
        Assert.Equal(0.80, probUp);
        Assert.Equal(0.10, probDown);
        Assert.Equal(0.10, probSideways);
        Assert.True(confidence > 0);
    }

    [Fact]
    public void AggregateLayers_AllLayersDegraded_ReturnsSafeNeutralFallbackWithoutCrash()
    {
        var layers = new[]
        {
            new EnsembleService.EnsembleLayerInput("Confluence", 0.50, null, null, null, null, null, IsAvailable: false),
            new EnsembleService.EnsembleLayerInput("Markov", 0.50, null, null, null, null, null, IsAvailable: false)
        };

        var (probUp, probDown, probSideways, direction, confidence, degraded, _) = EnsembleService.AggregateLayers(layers);

        Assert.Equal(2, degraded.Count);
        Assert.Equal("Sideways", direction);
        Assert.True(confidence > 0.30);
    }
}
