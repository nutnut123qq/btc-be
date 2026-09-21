using Backend.Data;

namespace Backend.Services.Models;

public sealed record ExecutionCostSpecification(
    string Instrument,
    double FeeBpsPerSide,
    double SlippageBpsPerSide)
{
    public double OneWayCostBps => FeeBpsPerSide + SlippageBpsPerSide;
    public double RoundTripCostBps => 2 * OneWayCostBps;
    public double RoundTripCostPct => BasisPoints.ToPercent(RoundTripCostBps);
    public double RoundTripCostFraction => BasisPoints.ToFraction(RoundTripCostBps);
}

public static class BasisPoints
{
    public static double ToPercent(double bps) => Validate(bps) / 100.0;
    public static double ToFraction(double bps) => Validate(bps) / 10_000.0;

    private static double Validate(double bps)
    {
        if (!double.IsFinite(bps) || bps < 0)
            throw new ArgumentOutOfRangeException(nameof(bps), "Basis points must be finite and non-negative.");
        return bps;
    }
}

public sealed class ResearchSpecificationDto
{
    public string ContractVersion { get; init; } = ResearchVersions.ResearchSpecification;
    public string Symbol { get; init; } = "BTCUSDT";
    public string Venue { get; init; } = "Binance";
    public string MarketType { get; init; } = "Spot";
    public string SourceTimeframe { get; init; } = "4h";
    public int HorizonBars { get; init; } = 1;
    public string HorizonElapsed { get; init; } = "4h";
    public string DecisionTimeRule { get; init; } = "After the source candle is finalized";
    public string OutcomeDefinition { get; init; } = "Next finalized 4h close-to-close return";
    public double DirectionLabelDeadZonePct { get; init; } = 0;
    public ExecutionCostSpecification Costs { get; init; } = new("BTCUSDT spot research reference", 10, 5);
    public string EvidenceMaturity { get; init; } = "Experimental";
    public bool PromotionEligible { get; init; }
    public string PromotionReason { get; init; } = "No component is approved for production: an offline predictive-evidence pass does not satisfy economic simulation or prospective forward-observation gates.";
}
