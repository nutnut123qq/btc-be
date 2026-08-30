using Backend.Options;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Backend.Controllers;

[ApiController]
[Route("api/telegram")]
public class TelegramController : ControllerBase
{
    private readonly ITelegramNotificationService _telegramService;
    private readonly TelegramOptions _options;

    public TelegramController(
        ITelegramNotificationService telegramService,
        IOptions<TelegramOptions> options)
    {
        _telegramService = telegramService;
        _options = options.Value;
    }

    [HttpPost("test")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> TestMessage()
    {
        var success = await _telegramService.SendMessageAsync("🔔 *Test Message*\nThis is a test notification from Bitcoin AI Analyst.");
        return Ok(new { success, message = success ? "Message sent successfully" : "Failed to send message or Telegram is not configured properly" });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var configured = !string.IsNullOrEmpty(_options.BotToken) && !string.IsNullOrEmpty(_options.ChatId);
        return Ok(new { enabled = _options.Enabled, configured });
    }
}
