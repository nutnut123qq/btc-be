namespace Backend.Services;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Backend.Services.Models;

public interface ITransitionService
{
    /// <summary>
    /// Gets top transitions from an archetype.
    /// </summary>
    Task<ArchetypeTransitionsResponse> GetTransitionsFromAsync(long archetypeId, int top, CancellationToken ct);

    /// <summary>
    /// Gets top transitions to an archetype.
    /// </summary>
    Task<ArchetypeTransitionsResponse> GetTransitionsToAsync(long archetypeId, int top, CancellationToken ct);

    /// <summary>
    /// Predicts next archetype based on current window.
    /// </summary>
    Task<TransitionPredictionDto> PredictNextAsync(string symbol, string timeframe, int windowSize, CancellationToken ct);

    /// <summary>
    /// Predicts next archetype based on a sequence of windows.
    /// </summary>
    Task<SequencePredictionDto> GetSequencePredictionAsync(string symbol, string timeframe, int windowSize, CancellationToken ct);

    /// <summary>
    /// Gets entropy ranking for archetypes.
    /// </summary>
    Task<EntropyRankingResponse> GetEntropyRankingAsync(string symbol, string timeframe, int? windowSize, int top, CancellationToken ct);

    /// <summary>
    /// Gets the full transition matrix.
    /// </summary>
    Task<TransitionMatrixDto> GetTransitionMatrixAsync(string symbol, string timeframe, int windowSize, CancellationToken ct);
}
