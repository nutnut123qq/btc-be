using Backend.Services.Models;

namespace Backend.Services;

public interface IBinanceUserDataStreamService
{
    bool IsConnected { get; }
    string? CurrentListenKey { get; }
    DateTimeOffset? LastPingTime { get; }
    DateTimeOffset? ConnectedSince { get; }
    int ReconnectCount { get; }

    StreamStatusDto GetStatus();

    Task<string?> CreateListenKeyAsync(CancellationToken cancellationToken = default);
    Task<bool> PingListenKeyAsync(string? listenKey = null, CancellationToken cancellationToken = default);
    Task<bool> CloseListenKeyAsync(string? listenKey = null, CancellationToken cancellationToken = default);
    Task ReconnectAsync(CancellationToken cancellationToken = default);

    event Func<OrderTradeUpdateEvent, Task>? OnOrderTradeUpdate;
    event Func<AccountUpdateEvent, Task>? OnAccountUpdate;
    event Func<string, Task>? OnListenKeyExpired;
    event Func<bool, Task>? OnConnectionStatusChanged;
}
