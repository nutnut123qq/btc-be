using Backend.Controllers;

namespace Backend.Tests;

public class EnsembleControllerTests
{
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
}
