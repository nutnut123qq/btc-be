using Backend.Data;
using Backend.Services.Models;

namespace Backend.Services;

public interface IUserDataStreamHandlerService
{
    Task HandleOrderTradeUpdateAsync(OrderTradeUpdateEvent orderEvent, CancellationToken ct = default);
    Task HandleAccountUpdateAsync(AccountUpdateEvent accountEvent, CancellationToken ct = default);
    Task<IReadOnlyList<WalletBalanceSnapshot>> GetBalanceSnapshotsAsync(string asset = "USDT", int limit = 100, CancellationToken ct = default);
}
