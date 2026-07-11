using Backend.Services.Models;

namespace Backend.Services;

public interface IDataAuditService
{
    Task<DataAuditResponse> AuditAsync(string symbol, CancellationToken cancellationToken = default);
}
