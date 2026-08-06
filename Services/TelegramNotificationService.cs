using System.Text;
using System.Text.Json;
using Backend.Options;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public class TelegramNotificationService : ITelegramNotificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramNotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(_options.BotToken) || string.IsNullOrEmpty(_options.ChatId))
        {
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Telegram");
            var url = $"https://api.telegram.org/bot{_options.BotToken}/sendMessage";
            
            var payload = new
            {
                chat_id = _options.ChatId,
                text = message,
                parse_mode = "Markdown"
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Telegram API returned non-success status code {StatusCode}. Response: {Response}", response.StatusCode, responseBody);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while sending Telegram message");
            return false;
        }
    }
}
