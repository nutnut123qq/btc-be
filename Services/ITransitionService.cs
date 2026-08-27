namespace Backend.Services;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Backend.Data;

public interface ITransitionService
{
    /// <summary>
    /// Gets top transitions from an archetype.
    /// </summary>
    Task<List<ArchetypeTransition>> GetTransitionsFromAsync(long archetypeId, int top, CancellationToken ct);

    /// <summary>
    /// Gets top transitions to an archetype.
    /// </summary>
    Task<List<ArchetypeTransition>> GetTransitionsToAsync(long archetypeId, int top, CancellationToken ct);

    /// <summary>
    /// Predicts next archetype based on current window.
    /// </summary>
    Task<List<ArchetypeTransition>> PredictNextAsync(string symbol, string timeframe, int windowSize, CancellationToken ct);

    /// <summary>
    /// Predicts next archetype based on a sequence of windows.
    /// </summary>
    Task<List<ArchetypeSequence>> GetSequencePredictionAsync(string symbol, string timeframe, int windowSize, CancellationToken ct);

    /// <summary>
    /// Gets entropy ranking for archetypes.
    /// </summary>
    Task<object> GetEntropyRankingAsync(string symbol, string timeframe, int? windowSize, int top, CancellationToken ct);

    /// <summary>
    /// Gets the full transition matrix.
    /// </summary>
    Task<List<ArchetypeTransition>> GetTransitionMatrixAsync(string symbol, string timeframe, int windowSize, CancellationToken ct);
}
