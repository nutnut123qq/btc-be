using Backend.Services.Models;

namespace Backend.Services;

public interface ITelegramNotificationService
{
    Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default);
    Task<bool> SendTradeExecutionAlertAsync(TradeExecutionAlertDto alert, CancellationToken cancellationToken = default);
    string FormatTradeExecutionMessage(TradeExecutionAlertDto alert);
}
