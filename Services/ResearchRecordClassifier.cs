using Backend.Data;

namespace Backend.Services;

internal static class ResearchRecordClassifier
{
    internal static (string Status, string? Reason) Classify(BacktestRun run)
    {
        if (run.ValidityStatus == ValidityStatuses.Invalid)
            return (ValidityStatuses.Invalid, run.InvalidReason ?? "Previously classified as invalid.");
        if (string.IsNullOrWhiteSpace(run.ModelName))
            return (ValidityStatuses.Invalid, "Model name is missing.");
        if (run.TotalTrades != run.Trades.Count)
            return (ValidityStatuses.Invalid, "Trade count does not match the stored trade ledger.");
        if (run.TotalTrades < 5 && Math.Abs(run.TotalReturnPct) > 100)
            return (ValidityStatuses.Invalid, "Implausible return with fewer than five trades.");
        if (!double.IsFinite(run.TotalReturnPct) || !double.IsFinite(run.FinalEquity))
            return (ValidityStatuses.Invalid, "Backtest contains a non-finite accounting value.");
        if (run.Trades.Any(trade => Math.Abs(trade.PnlPct - trade.NetReturn * 100) > 0.01))
            return (ValidityStatuses.Invalid, "TRADE_LEDGER_INCONSISTENT: PnL percent does not match net return.");

        if (run.Trades.Count > 0)
        {
            var ledgerReturnPct = (run.Trades.Aggregate(1.0, (equity, trade) => equity * (1 + trade.NetReturn)) - 1) * 100;
            if (Math.Abs(ledgerReturnPct - run.TotalReturnPct) > 0.5)
                return (ValidityStatuses.Invalid, "Reported return does not reconcile with the trade ledger.");
        }

        if (IsUnversioned(run.PipelineVersion) || IsUnversioned(run.EvaluationVersion))
            return (ValidityStatuses.Legacy, "Record predates versioned pipeline/evaluation evidence.");

        return (ValidityStatuses.Valid, null);
    }

    internal static (string Status, string? Reason) Classify(ModelPrediction prediction, bool duplicateSourceEvent)
    {
        if (prediction.ValidityStatus == ValidityStatuses.Invalid)
            return (ValidityStatuses.Invalid, prediction.InvalidReason ?? "Previously classified as invalid.");
        if (string.IsNullOrWhiteSpace(prediction.ModelVersion))
            return (ValidityStatuses.Invalid, "Model version is missing.");
        if (prediction.WindowEndMs <= 0
            || string.IsNullOrWhiteSpace(prediction.Symbol)
            || string.IsNullOrWhiteSpace(prediction.Timeframe)
            || string.IsNullOrWhiteSpace(prediction.Horizon))
            return (ValidityStatuses.Invalid, "PREDICTION_IDENTITY: Symbol, timeframe, horizon, and window end are required.");
        if (prediction.PredictedLabel is not (-1 or 0 or 1))
            return (ValidityStatuses.Invalid, "PREDICTED_LABEL: Label must be -1, 0, or 1.");
        var probabilities = new[] { prediction.ProbUp, prediction.ProbDown, prediction.ProbSideways };
        if (probabilities.Any(value => !double.IsFinite(value) || value is < 0 or > 1)
            || Math.Abs(probabilities.Sum() - 1) > 0.0015)
            return (ValidityStatuses.Invalid, "PROBABILITIES_NOT_NORMALIZED: Probabilities must sum to one within tolerance 0.0015.");
        if (duplicateSourceEvent)
            return (ValidityStatuses.Invalid, "DUPLICATE_SOURCE_EVENT: A structurally valid prediction already exists for this inference event.");
        if (IsUnversioned(prediction.PipelineVersion) || IsUnversioned(prediction.EvaluationVersion))
            return (ValidityStatuses.Legacy, "Prediction predates versioned pipeline/evaluation evidence.");
        return (ValidityStatuses.Valid, null);
    }

    internal static (string Status, string? Reason) Classify(EnsemblePredictionRecord prediction, bool duplicateSourceEvent)
    {
        if (prediction.ValidityStatus == ValidityStatuses.Invalid)
            return (ValidityStatuses.Invalid, prediction.InvalidReason ?? "Previously classified as invalid.");
        if (prediction.EntryPrice <= 0 || !double.IsFinite(prediction.EntryPrice))
            return (ValidityStatuses.Invalid, "ENTRY_PRICE: Entry price must be positive and finite.");
        if (!new[] { "Bullish", "Bearish", "Sideways" }.Contains(prediction.FinalDirection, StringComparer.OrdinalIgnoreCase))
            return (ValidityStatuses.Invalid, "FINAL_DIRECTION: Direction must be Bullish, Bearish, or Sideways.");
        if (prediction.EvaluationStatus is not ("T" or "F" or "N"))
            return (ValidityStatuses.Invalid, "EVALUATION_STATUS: Status must be T, F, or N.");
        var hasCompleteEvaluation = prediction.ActualPrice24h is > 0
            && prediction.ActualReturnPct.HasValue
            && prediction.EvaluatedAtMs.HasValue;
        if ((prediction.EvaluationStatus == "N" && (prediction.ActualPrice24h.HasValue || prediction.ActualReturnPct.HasValue || prediction.EvaluatedAtMs.HasValue))
            || (prediction.EvaluationStatus is "T" or "F" && !hasCompleteEvaluation))
            return (ValidityStatuses.Invalid, "EVALUATION_FIELDS: Evaluation fields do not match evaluation status.");
        var probabilities = new[] { prediction.ProbUp, prediction.ProbDown, prediction.ProbSideways };
        if (probabilities.Any(value => !double.IsFinite(value) || value is < 0 or > 1)
            || Math.Abs(probabilities.Sum() - 1) > 0.0015)
            return (ValidityStatuses.Invalid, "PROBABILITIES_NOT_NORMALIZED: Probabilities must sum to one within tolerance 0.0015.");
        if (duplicateSourceEvent)
            return (ValidityStatuses.Invalid, "DUPLICATE_SOURCE_EVENT: A structurally valid record already exists for this symbol/timeframe/timestamp.");
        if (IsUnversioned(prediction.PipelineVersion) || IsUnversioned(prediction.EvaluationVersion))
            return (ValidityStatuses.Legacy, "Ensemble record predates point-in-time versioned evaluation.");
        return (ValidityStatuses.Valid, null);
    }

    internal static bool IsStructurallyValid(ModelPrediction item)
    {
        var probabilities = new[] { item.ProbUp, item.ProbDown, item.ProbSideways };
        return item.WindowEndMs > 0
            && !string.IsNullOrWhiteSpace(item.Symbol)
            && !string.IsNullOrWhiteSpace(item.Timeframe)
            && !string.IsNullOrWhiteSpace(item.Horizon)
            && !string.IsNullOrWhiteSpace(item.ModelVersion)
            && item.PredictedLabel is -1 or 0 or 1
            && probabilities.All(value => double.IsFinite(value) && value is >= 0 and <= 1)
            && Math.Abs(probabilities.Sum() - 1) <= 0.0015;
    }

    internal static bool IsStructurallyValid(EnsemblePredictionRecord item)
    {
        var probabilities = new[] { item.ProbUp, item.ProbDown, item.ProbSideways };
        var evaluationValid = item.EvaluationStatus switch
        {
            "N" => !item.ActualPrice24h.HasValue && !item.ActualReturnPct.HasValue && !item.EvaluatedAtMs.HasValue,
            "T" or "F" => item.ActualPrice24h is > 0 && item.ActualReturnPct.HasValue && item.EvaluatedAtMs.HasValue,
            _ => false
        };
        return item.EntryPrice > 0
            && double.IsFinite(item.EntryPrice)
            && new[] { "Bullish", "Bearish", "Sideways" }.Contains(item.FinalDirection, StringComparer.OrdinalIgnoreCase)
            && evaluationValid
            && probabilities.All(value => double.IsFinite(value) && value is >= 0 and <= 1)
            && Math.Abs(probabilities.Sum() - 1) <= 0.0015;
    }

    private static bool IsUnversioned(string? version) =>
        string.IsNullOrWhiteSpace(version)
        || string.Equals(version, ResearchVersions.Legacy, StringComparison.OrdinalIgnoreCase);
}
