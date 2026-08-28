using System.Globalization;
using System.Text;
using System.Text.Json;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.Extensions.Logging;
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
            _logger.LogInformation("[Telegram] Telegram notifications disabled or not configured. Message skipped.");
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
                parse_mode = "HTML"
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[Telegram] Tin nhắn thông báo khớp lệnh gửi thành công tới ChatId={ChatId}", _options.ChatId);
                return true;
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("[Telegram] Telegram API returned non-success status code {StatusCode}. Response: {Response}", response.StatusCode, responseBody);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Telegram] Exception occurred while sending Telegram message");
            return false;
        }
    }

    public async Task<bool> SendTradeExecutionAlertAsync(TradeExecutionAlertDto alert, CancellationToken cancellationToken = default)
    {
        var message = FormatTradeExecutionMessage(alert);
        _logger.LogInformation("[Telegram] Chuẩn bị gửi cảnh báo khớp lệnh:\n{Message}", message);
        return await SendMessageAsync(message, cancellationToken);
    }

    public string FormatTradeExecutionMessage(TradeExecutionAlertDto alert)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🔔 [BINANCE TESTNET EXECUTION ALERT]");
        sb.AppendLine($"💎 Cặp: #{alert.Symbol} | Vị thế: {alert.Side.ToUpperInvariant()}");
        sb.AppendLine($"🎯 Trạng thái: {alert.Status.ToUpperInvariant()}");

        if (alert.IsExit && alert.ExitPrice.HasValue)
        {
            sb.AppendLine($"💵 Giá vào: ${alert.EntryPrice:N2} → Giá đóng: ${alert.ExitPrice.Value:N2}");

            double pnl = alert.RealizedPnL ?? 0.0;
            double roi = alert.RoiPercent ?? 0.0;
            string pnlSign = pnl >= 0 ? "+" : "-";
            string roiSign = roi >= 0 ? "+" : "";

            sb.AppendLine($"💰 Lãi/Lỗ: {pnlSign}${Math.Abs(pnl):N2} USDT ({roiSign}{roi:F2}% ROI)");

            if (!string.IsNullOrWhiteSpace(alert.DurationText))
            {
                sb.AppendLine($"⏱ Thời gian nắm giữ: {alert.DurationText}");
            }
        }
        else
        {
            sb.AppendLine($"💵 Giá vào: ${alert.EntryPrice:N2}");
            sb.AppendLine($"📦 Khối lượng: {alert.ExecutedQty:F4} {alert.Symbol.Replace("USDT", "")}");
            sb.AppendLine($"⏱ Thời gian: {alert.Timestamp:HH:mm:ss dd/MM/yyyy} UTC");
        }

        return sb.ToString().TrimEnd();
    }
}
