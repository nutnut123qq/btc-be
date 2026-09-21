using Backend.Data;
using Backend.Services.Models;

namespace Backend.Services;

public interface ITechnicalCapabilityRegistry
{
    TechnicalCapabilitiesResponse GetSnapshot();
}

/// <summary>
/// Versioned, read-only catalogue of technical capabilities. This deliberately
/// describes evidence maturity without deriving or inventing live performance metrics.
/// </summary>
public sealed class TechnicalCapabilityRegistry : ITechnicalCapabilityRegistry
{
    private static readonly IReadOnlyList<TechnicalCapabilityDto> Items = Array.AsReadOnly(
    [
        Capability("market-data", "BTC market data", "data", "operational", "validated",
            "Collect finalized BTCUSDT candles and expose current market observations.",
            "Research scope is BTCUSDT on active 1h, 4h and 1d candle timeframes; order-book data is a snapshot rather than a sequence-verified feed.",
            "/api/market/btc/klines", "btc-market-data-v2"),
        Capability("data-audit", "Data quality audit", "data", "operational", "validated",
            "Measure gaps, invalid rows, duplicates, forming candles, staleness and derived-data coverage.",
            "An audit reports observed quality; it cannot reconstruct missing exchange history or prove external source correctness.",
            "/api/market/data-audit", "data-audit-v2"),
        Capability("technical-indicators", "Technical indicators", "features", "operational", "validated",
            "Compute causal trend, momentum, volatility and volume features from finalized candles.",
            "Indicators describe transformations of OHLCV and do not independently establish predictive or trading value.",
            "/api/market/btc/tech-summary", ResearchVersions.DataPipeline),
        Capability("volume-anomaly", "Volume anomaly", "features", "operational", "descriptive",
            "Compare candle volume with its historical context for screening and research.",
            "Anomalous volume does not identify trade direction; current familywise technical-event evidence is inconclusive.",
            "/api/discovery/volume-stats", "volume-anomaly-v1"),
        Capability("candle-patterns", "Candle patterns", "features", "operational", "descriptive",
            "Encode classical candle shapes and sequences as reproducible research features.",
            "Pattern recognition is shape classification; current familywise technical-event evidence supports no approved predictive claim.",
            "/api/market/candle-patterns", "candle-patterns-v2"),
        Capability("market-regime", "Market regime", "features", "operational", "experimental",
            "Segment BTC history into comparable trend and volatility conditions.",
            "Regime labels are model-dependent and current event evidence has not independently established predictive or economic value.",
            "/api/regime/current", "market-regime-v1"),
        Capability("smart-money", "Market structure and SMC", "features", "operational", "descriptive",
            "Describe confirmed swings, breaks of structure and fair-value gaps with causal availability times.",
            "These geometric constructs do not demonstrate institutional activity or a profitable signal by themselves.",
            "/api/smart-money/structures", "smart-money-causal-v2"),
        Capability("volume-profile", "Volume profile estimate", "features", "operational", "descriptive",
            "Estimate price-area volume concentration from candle OHLCV for exploratory context.",
            "Candle volume is allocated across each high-low range; this is not observed traded-at-price volume.",
            "/api/volume-profile/current", "ohlcv-uniform-range-v1"),
        Capability("futures-metrics", "Futures metrics", "data", "degraded", "descriptive",
            "Add funding, open-interest and positioning context using records with explicit event and availability times.",
            "Legacy reconstructed rows lack trustworthy receipt or availability timestamps and are excluded from causal as-of use.",
            "/api/market/btc/tech-summary", "derivative-lineage-v1"),
        Capability("liquidation-estimates", "Liquidation estimates", "features", "operational", "descriptive",
            "Explore hypothetical leveraged liquidation density around observed BTC prices.",
            "Levels are model estimates from assumptions and are not exchange-observed liquidation orders.",
            "/api/liquidation/latest", "liquidation-estimator-v1"),
        Capability("historical-analog", "Historical analogs", "research", "operational", "experimental",
            "Retrieve causal historical neighbours for hypothesis generation and comparison.",
            "The current version has low accepted coverage and no approved predictive or economic edge.",
            "/api/historical-analogs", ResearchVersions.HistoricalAnalogApiContract),
        Capability("temporal-archetype", "Temporal archetypes", "research", "operational", "experimental",
            "Group comparable temporal windows and inspect conditional outcome distributions.",
            "Offline probability improvement is small and has not established directional or post-cost trading value.",
            "/api/archetypes/match", "temporal-archetype-v1"),
        Capability("markov-transitions", "Archetype transitions", "research", "unavailable", "experimental",
            "Study transition counts and conditional next-state distributions between frozen archetypes.",
            "No production-ready transition matrix with sufficient evidence is currently available; prediction may abstain.",
            "/api/transitions/predict", "archetype-transition-v1"),
        Capability("rule-discovery", "Rule discovery", "research", "operational", "experimental",
            "Generate and evaluate a bounded ledger of hypotheses with chronological selection and out-of-sample partitions.",
            "Discovered rules remain experimental and disabled until independent evidence and prospective observation gates pass.",
            "/api/discovery/rules", "rule-discovery-oos-v2"),
        Capability("confluence", "Technical confluence", "decision-support", "operational", "descriptive",
            "Summarize several technical observations into a human-readable decision-support score.",
            "The score is a heuristic, not a calibrated probability, and correlated inputs can repeat the same information.",
            "/api/confluence/current", "confluence-descriptive-v1"),
        Capability("ml-prediction", "Machine-learning prediction", "research", "operational", "experimental",
            "Evaluate versioned BTC direction candidates with walk-forward, calibrated probabilistic evidence.",
            "The current candidate passed its declared historical predictive gate; this does not satisfy economic simulation or prospective promotion gates.",
            "/api/prediction/latest", ResearchVersions.Evaluation),
        Capability("ensemble", "Model ensemble", "decision-support", "unavailable", "experimental",
            "Combine eligible component predictions only when their inputs and evidence satisfy the research contract.",
            "No ensemble is approved for production; the endpoint abstains when eligible evidence is unavailable.",
            "/api/ensemble/predict", ResearchVersions.EnsembleApiContract),
        Capability("historical-replay", "Historical execution replay", "execution-research", "operational", "experimental",
            "Evaluate frozen candidates with next-bar fills, explicit costs, capital, positions and mark-to-market accounting.",
            "Candle data cannot resolve intrabar event order, and replay results are not prospective evidence.",
            "/api/backtest/runs", "execution-replay-v2"),
        Capability("forward-paper", "Forward paper observation", "execution-research", "operational", "experimental",
            "Record one timestamped decision, abstention and live quote for the latest finalized BTC 4h signal bar observed by each poll.",
            "The recorder does not backfill bars missed during downtime. It currently captures prospective decision evidence only; fill and outcome observers remain empty, so it makes no PnL claim.",
            "/api/paper-observations", "forward-paper-observation-v1"),
        Capability("alerts", "Technical alerts", "operations", "operational", "validated",
            "Deliver deduplicated technical events with evidence provenance, availability time and delivery state.",
            "Delivery correctness does not validate the predictive or economic value of the underlying signal.",
            "/api/alerts", "evidence-alerts-v2")
    ]);

    private readonly TimeProvider _timeProvider;

    public TechnicalCapabilityRegistry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        ValidateRegistry(Items);
    }

    public TechnicalCapabilitiesResponse GetSnapshot() =>
        TechnicalCapabilitiesResponse.Create(_timeProvider.GetUtcNow().UtcDateTime, Items);

    private static TechnicalCapabilityDto Capability(
        string id,
        string name,
        string category,
        string operationalStatus,
        string evidenceStage,
        string intendedUse,
        string limitation,
        string endpoint,
        string version) => new(
            id, name, category, operationalStatus, evidenceStage, EvidenceTargetFor(id),
            intendedUse, limitation, endpoint, version);

    private static string EvidenceTargetFor(string id) => id switch
    {
        "market-data" or "data-audit" or "futures-metrics" => CapabilityEvidenceTargets.DataIntegrity,
        "volume-anomaly" or "candle-patterns" or "market-regime" or "smart-money"
            or "volume-profile" or "liquidation-estimates" or "confluence"
            or "technical-indicators" => CapabilityEvidenceTargets.CalculationCorrectness,
        "historical-analog" or "temporal-archetype" or "markov-transitions"
            or "rule-discovery" or "ml-prediction" or "ensemble" => CapabilityEvidenceTargets.Predictive,
        "historical-replay" => CapabilityEvidenceTargets.EconomicSimulation,
        "forward-paper" => CapabilityEvidenceTargets.ProspectiveObservation,
        "alerts" => CapabilityEvidenceTargets.OperationalDelivery,
        _ => throw new InvalidOperationException($"Technical capability '{id}' has no evidence target.")
    };

    private static void ValidateRegistry(IReadOnlyList<TechnicalCapabilityDto> items)
    {
        if (items.Count == 0 || items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new InvalidOperationException("Technical capability ids must be non-empty and unique.");

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)
                || string.IsNullOrWhiteSpace(item.Name)
                || string.IsNullOrWhiteSpace(item.Category)
                || string.IsNullOrWhiteSpace(item.IntendedUse)
                || string.IsNullOrWhiteSpace(item.Limitation)
                || string.IsNullOrWhiteSpace(item.Endpoint)
                || string.IsNullOrWhiteSpace(item.Version))
                throw new InvalidOperationException($"Technical capability '{item.Id}' has incomplete metadata.");
            if (!CapabilityOperationalStatuses.All.Contains(item.OperationalStatus))
                throw new InvalidOperationException($"Technical capability '{item.Id}' has an invalid operational status.");
            if (!CapabilityEvidenceStages.All.Contains(item.EvidenceStage))
                throw new InvalidOperationException($"Technical capability '{item.Id}' has an invalid evidence stage.");
            if (!CapabilityEvidenceTargets.All.Contains(item.EvidenceTarget))
                throw new InvalidOperationException($"Technical capability '{item.Id}' has an invalid evidence target.");
            if (!item.Endpoint.StartsWith("/api/", StringComparison.Ordinal))
                throw new InvalidOperationException($"Technical capability '{item.Id}' has an invalid endpoint.");
        }
    }
}
