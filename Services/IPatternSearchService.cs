using Backend.Services.Models;

namespace Backend.Services;

public interface IPatternSearchService
{
    Task<PatternSearchResponse> SearchAsync(PatternSearchRequest request, string requestId, CancellationToken cancellationToken = default);
}
