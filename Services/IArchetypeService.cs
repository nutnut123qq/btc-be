using Backend.Services.Models;

namespace Backend.Services;

public interface IArchetypeService
{
    Task<(int Total, List<ArchetypeDto> Items)> GetArchetypesAsync(string symbol, string timeframe, int? windowSize, string sortBy, int page, int pageSize, CancellationToken ct = default);
    Task<ArchetypeDetailDto?> GetArchetypeDetailAsync(long id, CancellationToken ct = default);
    Task<ArchetypeMatchDto?> MatchCurrentWindowAsync(string symbol, string timeframe, int windowSize, CancellationToken ct = default);
    Task<(List<ArchetypeMatchDto> Matches, object WeightedSignal)> MatchMultiWindowAsync(string symbol, string timeframe, CancellationToken ct = default);
    Task<(int Total, List<ArchetypeOccurrenceDto> Items)> GetOccurrencesAsync(long archetypeId, string horizon, int page, int pageSize, CancellationToken ct = default);
    Task<List<ArchetypeRankingDto>> GetRankingsAsync(string symbol, string timeframe, int? windowSize, string? horizon, string sortBy, int top, CancellationToken ct = default);
}
