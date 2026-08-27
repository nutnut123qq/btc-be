using Microsoft.AspNetCore.SignalR;

namespace Backend.Hubs;

public class TradeNotificationHub : Hub
{
    public const string HubUrl = "/hubs/trade-notifications";

    public async Task JoinTradeStream()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "TradeStream");
    }

    public async Task LeaveTradeStream()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "TradeStream");
    }
}
