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

    public async Task<List<ArchetypeTransition>> GetTransitionsFromAsync(long archetypeId, int top, CancellationToken ct)
    {
        return await _db.ArchetypeTransitions
            .AsNoTracking()
            .Include(x => x.ToArchetype)
            .Where(x => x.FromArchetypeId == archetypeId)
            .OrderByDescending(x => x.TransitionProbability)
            .Take(top)
            .ToListAsync(ct);
    }

    public async Task<List<ArchetypeTransition>> GetTransitionsToAsync(long archetypeId, int top, CancellationToken ct)
    {
        return await _db.ArchetypeTransitions
            .AsNoTracking()
            .Include(x => x.FromArchetype)
            .Where(x => x.ToArchetypeId == archetypeId)
            .OrderByDescending(x => x.TransitionProbability)
            .Take(top)
            .ToListAsync(ct);
    }

    public async Task<List<ArchetypeTransition>> PredictNextAsync(string symbol, string timeframe, int windowSize, CancellationToken ct)
    {
        var match = await _archetypeService.MatchCurrentWindowAsync(symbol, timeframe, windowSize, ct);
        if (match == null || match.Archetype == null)
            return new List<ArchetypeTransition>();

        return await GetTransitionsFromAsync(match.Archetype.Id, 10, ct);
    }

    public async Task<List<ArchetypeSequence>> GetSequencePredictionAsync(string symbol, string timeframe, int windowSize, CancellationToken ct)
    {
        // Simplistic approach for now, normally we'd need to fetch actual last 2 archetypes.
        // Assuming we just want to return sequences ending with the current matched archetype
        var match = await _archetypeService.MatchCurrentWindowAsync(symbol, timeframe, windowSize, ct);
        if (match == null || match.Archetype == null)
            return new List<ArchetypeSequence>();

        return await _db.ArchetypeSequences
            .AsNoTracking()
            .Include(x => x.FirstArchetype)
            .Include(x => x.SecondArchetype)
            .Include(x => x.ThirdArchetype)
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize && (x.FirstArchetypeId == match.Archetype.Id || x.SecondArchetypeId == match.Archetype.Id))
            .OrderByDescending(x => x.OccurrenceCount)
            .Take(10)
            .ToListAsync(ct);
    }

    public async Task<object> GetEntropyRankingAsync(string symbol, string timeframe, int? windowSize, int top, CancellationToken ct)
    {
        var query = _db.ArchetypeTransitions
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe);

        if (windowSize.HasValue)
        {
            query = query.Where(x => x.WindowSize == windowSize.Value);
        }

        var transitions = await query.ToListAsync(ct);

        var grouped = transitions.GroupBy(x => x.FromArchetypeId);
        var entropyList = new List<object>();

        foreach (var group in grouped)
        {
            double entropy = 0;
            foreach (var t in group)
            {
                if (t.TransitionProbability > 0)
                {
                    entropy -= t.TransitionProbability * Math.Log(t.TransitionProbability, 2);
                }
            }
            entropyList.Add(new { ArchetypeId = group.Key, Entropy = entropy });
        }

        return entropyList.OrderByDescending(x => ((dynamic)x).Entropy).Take(top).ToList();
    }

    public async Task<List<ArchetypeTransition>> GetTransitionMatrixAsync(string symbol, string timeframe, int windowSize, CancellationToken ct)
    {
        return await _db.ArchetypeTransitions
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize)
            .ToListAsync(ct);
    }
}
