using Backend.Services.Models;

namespace Backend.Services;

public interface ICandleSequenceRulesEngine
{
    Task<IReadOnlyList<CandleSequenceRuleSignalDto>> EvaluateAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default);
}
