namespace Backend.Services;

public interface IRagService
{
    Task<string> BuildNewsContextAsync(string query, int topK = 8, CancellationToken cancellationToken = default);
}
