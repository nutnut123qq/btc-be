using Backend.Services.Models;

namespace Backend.Services;

public interface IDataAuditService
{
    Task<DataAuditResponse> AuditAsync(string symbol, bool includeInventory = false, CancellationToken cancellationToken = default);
    void Invalidate(string symbol);
}
