using System.Net;
using System.Text.Json;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.Tests;

public class TelegramNotificationServiceTests
{
    private class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public MockHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> syncHandler)
        {
            _handler = req => Task.FromResult(syncHandler(req));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    [Fact]
    public void FormatTradeExecutionMessage_TakeProfitOrder_FormatsExpectedTelegramMarkdown()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions());
        var factory = new MockHttpClientFactory(new HttpClient());
        var service = new TelegramNotificationService(factory, options, NullLogger<TelegramNotificationService>.Instance);

        var alert = new TradeExecutionAlertDto
        {
            Symbol = "BTCUSDT",
            Side = "LONG",
            Status = "TAKE PROFIT FILLED",
            EntryPrice = 72500.00,
            ExitPrice = 74200.00,
            ExecutedQty = 0.05,
            RealizedPnL = 142.50,
            RoiPercent = 2.35,
            DurationText = "8h 15m",
            IsExit = true
        };

        var message = service.FormatTradeExecutionMessage(alert);

        Assert.Contains("🔔 [BINANCE TESTNET EXECUTION ALERT]", message);
        Assert.Contains("💎 Cặp: #BTCUSDT | Vị thế: LONG", message);
        Assert.Contains("🎯 Trạng thái: TAKE PROFIT FILLED", message);
        Assert.Contains("💵 Giá vào: $72,500.00 -> Giá đóng: $74,200.00", message);
        Assert.Contains("💰 Lãi/Lỗ: +$142.50 USDT (+2.35% ROI)", message);
        Assert.Contains("⏱ Thời gian nắm giữ: 8h 15m", message);
    }

    [Fact]
    public void FormatTradeExecutionMessage_StopLossOrder_FormatsExpectedNegativePnL()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions());
        var factory = new MockHttpClientFactory(new HttpClient());
        var service = new TelegramNotificationService(factory, options, NullLogger<TelegramNotificationService>.Instance);

        var alert = new TradeExecutionAlertDto
        {
            Symbol = "BTCUSDT",
            Side = "SHORT",
            Status = "STOP LOSS FILLED",
            EntryPrice = 64000.00,
            ExitPrice = 65200.00,
            ExecutedQty = 0.10,
            RealizedPnL = -120.00,
            RoiPercent = -1.875,
            DurationText = "2h 45m",
            IsExit = true
        };

        var message = service.FormatTradeExecutionMessage(alert);

        Assert.Contains("💎 Cặp: #BTCUSDT | Vị thế: SHORT", message);
        Assert.Contains("🎯 Trạng thái: STOP LOSS FILLED", message);
        Assert.Contains("💵 Giá vào: $64,000.00 -> Giá đóng: $65,200.00", message);
        Assert.Contains("💰 Lãi/Lỗ: -$120.00 USDT (-1.88% ROI)", message);
        Assert.Contains("⏱ Thời gian nắm giữ: 2h 45m", message);
    }

    [Fact]
    public void FormatTradeExecutionMessage_PositionOpened_FormatsEntryDetails()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions());
        var factory = new MockHttpClientFactory(new HttpClient());
        var service = new TelegramNotificationService(factory, options, NullLogger<TelegramNotificationService>.Instance);

        var alert = new TradeExecutionAlertDto
        {
            Symbol = "BTCUSDT",
            Side = "LONG",
            Status = "POSITION OPENED (FILLED)",
            EntryPrice = 65000.00,
            ExecutedQty = 0.050,
            IsExit = false,
            Timestamp = new DateTimeOffset(2026, 8, 25, 14, 30, 0, TimeSpan.Zero)
        };

        var message = service.FormatTradeExecutionMessage(alert);

        Assert.Contains("💎 Cặp: #BTCUSDT | Vị thế: LONG", message);
        Assert.Contains("🎯 Trạng thái: POSITION OPENED (FILLED)", message);
        Assert.Contains("💵 Giá vào: $65,000.00", message);
        Assert.Contains("📦 Khối lượng: 0.0500 BTC", message);
    }

    [Fact]
    public async Task SendMessageAsync_WhenDisabled_ReturnsFalse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions
        {
            Enabled = false,
            BotToken = "123:ABC",
            ChatId = "-100123"
        });

        var factory = new MockHttpClientFactory(new HttpClient());
        var service = new TelegramNotificationService(factory, options, NullLogger<TelegramNotificationService>.Instance);

        var result = await service.SendMessageAsync("test");
        Assert.False(result);
    }

    [Fact]
    public async Task SendTradeExecutionAlertAsync_WhenEnabledAndApiSucceeds_ReturnsTrue()
    {
        string? capturedUrl = null;
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(async req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            capturedBody = req.Content != null ? await req.Content.ReadAsStringAsync() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\": true, \"result\": {\"message_id\": 999}}")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions
        {
            Enabled = true,
            BotToken = "test_bot_token_123",
            ChatId = "test_chat_id_456"
        });

        var factory = new MockHttpClientFactory(new HttpClient(handler));
        var service = new TelegramNotificationService(factory, options, NullLogger<TelegramNotificationService>.Instance);

        var alert = new TradeExecutionAlertDto
        {
            Symbol = "BTCUSDT",
            Side = "LONG",
            Status = "TAKE PROFIT FILLED",
            EntryPrice = 72500.00,
            ExitPrice = 74200.00,
            ExecutedQty = 0.05,
            RealizedPnL = 142.50,
            RoiPercent = 2.35,
            DurationText = "8h 15m",
            IsExit = true
        };

        var result = await service.SendTradeExecutionAlertAsync(alert);

        Assert.True(result);
        Assert.Equal("https://api.telegram.org/bottest_bot_token_123/sendMessage", capturedUrl);
        Assert.NotNull(capturedBody);
        Assert.Contains("test_chat_id_456", capturedBody);
        Assert.Contains("BINANCE TESTNET EXECUTION ALERT", capturedBody);
    }
}
