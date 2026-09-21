using Backend.Controllers;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Backend.Tests;

public class ResearchSpecificationTests
{
    [Fact]
    public void CostSpecification_UsesUnitSafeRoundTripConversions()
    {
        var costs = new ExecutionCostSpecification("BTCUSDT spot", 10, 5);

        Assert.Equal(30, costs.RoundTripCostBps);
        Assert.Equal(0.30, costs.RoundTripCostPct, 8);
        Assert.Equal(0.003, costs.RoundTripCostFraction, 8);
    }

    [Fact]
    public void Specification_FreezesBtcFourHourNextBarBenchmark()
    {
        var result = Assert.IsType<OkObjectResult>(new ResearchController().GetSpecification().Result);
        var specification = Assert.IsType<ResearchSpecificationDto>(result.Value);

        Assert.Equal("BTCUSDT", specification.Symbol);
        Assert.Equal("4h", specification.SourceTimeframe);
        Assert.Equal(1, specification.HorizonBars);
        Assert.Equal("4h", specification.HorizonElapsed);
        Assert.NotEqual(specification.DirectionLabelDeadZonePct, specification.Costs.RoundTripCostPct);
    }
}
