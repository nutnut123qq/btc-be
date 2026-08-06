namespace Backend.Services;

public interface ITelegramNotificationService
{
    Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default);
}
