using Backend.Services.Models;

namespace Backend.Services;

public interface IHistoricalAnalogService
{
    Task<HistoricalAnalogResponse> SearchAsync(
        HistoricalAnalogRequest request,
        string requestId,
        CancellationToken cancellationToken = default);
}
