namespace Backend.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Backend.Data;
using Backend.Services.Models;

public class TransitionService : ITransitionService
{
    private readonly AppDbContext _db;
    private readonly IArchetypeService _archetypeService;
    private readonly ILogger<TransitionService> _logger;

    public TransitionService(AppDbContext db, IArchetypeService archetypeService, ILogger<TransitionService> logger)
    {
        _db = db;
        _archetypeService = archetypeService;
        _logger = logger;
    }

    public async Task<ArchetypeTransitionsResponse> GetTransitionsFromAsync(long archetypeId, int top, CancellationToken ct)
    {
        var transitions = await _db.ArchetypeTransitions
            .AsNoTracking()
            .Include(x => x.FromArchetype)
            .Include(x => x.ToArchetype)
            .Where(x => x.FromArchetypeId == archetypeId)
            .OrderByDescending(x => x.TransitionProbability)
            .Take(Math.Clamp(top, 1, 100))
            .ToListAsync(ct);

        return new ArchetypeTransitionsResponse
        {
            ArchetypeId = archetypeId,
            Transitions = transitions.Select(MapTransition).ToList()
        };
    }

    public async Task<ArchetypeTransitionsResponse> GetTransitionsToAsync(long archetypeId, int top, CancellationToken ct)
    {
        var transitions = await _db.ArchetypeTransitions
            .AsNoTracking()
            .Include(x => x.FromArchetype)
            .Include(x => x.ToArchetype)
            .Where(x => x.ToArchetypeId == archetypeId)
            .OrderByDescending(x => x.TransitionProbability)
            .Take(Math.Clamp(top, 1, 100))
            .ToListAsync(ct);

        return new ArchetypeTransitionsResponse
        {
            ArchetypeId = archetypeId,
            Transitions = transitions.Select(MapTransition).ToList()
        };
    }

    public async Task<TransitionPredictionDto> PredictNextAsync(string symbol, string timeframe, int windowSize, CancellationToken ct)
    {
        var match = await _archetypeService.MatchCurrentWindowAsync(symbol, timeframe, windowSize, ct);
        if (match == null || match.Archetype == null)
        {
            return new TransitionPredictionDto
            {
                Validated = false,
                Reason = "No current archetype match is available for this symbol, timeframe, and window size."
            };
        }

        var response = await GetTransitionsFromAsync(match.Archetype.Id, 10, ct);
        var entropy = CalculateEntropy(response.Transitions.Select(x => x.TransitionProbability));
        return new TransitionPredictionDto
        {
            CurrentArchetypeId = match.Archetype.Id,
            CurrentArchetypeCode = match.Archetype.ArchetypeCode,
            Similarity = match.Similarity,
            TopTransitions = response.Transitions,
            EntropyBits = entropy,
            Predictability = "Unavailable",
            Validated = false,
            Reason = response.Transitions.Count == 0
                ? "Experimental transition statistics have no outgoing observations and have not passed out-of-sample promotion gates."
                : "Experimental transition statistics have not passed out-of-sample promotion gates."
        };
    }

    public async Task<SequencePredictionDto> GetSequencePredictionAsync(string symbol, string timeframe, int windowSize, CancellationToken ct)
    {
        var match = await _archetypeService.MatchCurrentWindowAsync(symbol, timeframe, windowSize, ct);
        return new SequencePredictionDto
        {
            CurrentArchetypeCode = match?.Archetype?.ArchetypeCode,
            Validated = false,
            Reason = "Sequence prediction is unavailable because the pipeline does not persist a validated previous-current archetype state."
        };
    }

    public async Task<EntropyRankingResponse> GetEntropyRankingAsync(string symbol, string timeframe, int? windowSize, int top, CancellationToken ct)
    {
        var query = _db.ArchetypeTransitions
            .AsNoTracking()
            .Include(x => x.FromArchetype)
            .Include(x => x.ToArchetype)
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe);

        if (windowSize.HasValue)
        {
            query = query.Where(x => x.WindowSize == windowSize.Value);
        }

        var transitions = await query.ToListAsync(ct);

        var ranked = transitions
            .GroupBy(x => x.FromArchetypeId)
            .Select(group =>
            {
                var first = group.First();
                var best = group.OrderByDescending(x => x.TransitionProbability).First();
                var entropy = CalculateEntropy(group.Select(x => x.TransitionProbability));
                return new EntropyRankingItemDto
                {
                    ArchetypeId = group.Key,
                    ArchetypeCode = first.FromArchetype?.ArchetypeCode ?? "",
                    Timeframe = first.Timeframe,
                    WindowSize = first.WindowSize,
                    MemberCount = first.FromArchetype?.MemberCount ?? 0,
                    EntropyBits = entropy,
                    Predictability = "Unavailable",
                    TopTransitionCode = best.ToArchetype?.ArchetypeCode ?? "",
                    TopTransitionProb = best.TransitionProbability
                };
            })
            .OrderBy(x => x.EntropyBits)
            .Take(Math.Clamp(top, 1, 200))
            .ToList();

        for (var i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        return new EntropyRankingResponse { Items = ranked };
    }

    public async Task<TransitionMatrixDto> GetTransitionMatrixAsync(string symbol, string timeframe, int windowSize, CancellationToken ct)
    {
        var transitions = await _db.ArchetypeTransitions
            .AsNoTracking()
            .Include(x => x.FromArchetype)
            .Include(x => x.ToArchetype)
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize)
            .ToListAsync(ct);

        return new TransitionMatrixDto
        {
            Symbol = symbol,
            Timeframe = timeframe,
            WindowSize = windowSize,
            ArchetypeCount = transitions
                .SelectMany(x => new[] { x.FromArchetypeId, x.ToArchetypeId })
                .Distinct()
                .Count(),
            TotalTransitions = transitions.Sum(x => x.TransitionCount),
            Cells = transitions.Select(x => new TransitionMatrixCellDto
            {
                FromId = x.FromArchetypeId,
                FromCode = x.FromArchetype?.ArchetypeCode ?? "",
                ToId = x.ToArchetypeId,
                ToCode = x.ToArchetype?.ArchetypeCode ?? "",
                Probability = x.TransitionProbability,
                Count = x.TransitionCount
            }).ToList()
        };
    }

    private static ArchetypeTransitionDto MapTransition(ArchetypeTransition transition) => new()
    {
        Id = transition.Id,
        FromArchetypeId = transition.FromArchetypeId,
        FromArchetypeCode = transition.FromArchetype?.ArchetypeCode ?? "",
        ToArchetypeId = transition.ToArchetypeId,
        ToArchetypeCode = transition.ToArchetype?.ArchetypeCode ?? "",
        TransitionCount = transition.TransitionCount,
        TransitionProbability = transition.TransitionProbability,
        AvgReturnPct = transition.AvgReturnPct,
        AvgBarsToTransition = transition.AvgBarsToTransition,
        LastSeenMs = transition.LastSeenMs
    };

    private static double CalculateEntropy(IEnumerable<double> probabilities) => probabilities
        .Where(x => x > 0)
        .Sum(x => -x * Math.Log(x, 2));

}
