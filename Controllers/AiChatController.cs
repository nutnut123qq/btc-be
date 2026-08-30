using System.Text;
using System.Text.Json;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/ai-chat")]
public class AiChatController : ControllerBase
{
    private readonly IAiContextService _contextService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiChatController> _logger;

    public AiChatController(
        IAiContextService contextService,
        IHttpClientFactory httpClientFactory,
        ILogger<AiChatController> logger)
    {
        _contextService = contextService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpPost("query")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    public async Task<IActionResult> QueryAi([FromBody] AiChatQueryDto request, CancellationToken ct)
    {
        string symbol = request.Symbol ?? "BTCUSDT";
        string timeframe = request.Timeframe ?? "1h";
        string userQuestion = string.IsNullOrWhiteSpace(request.Prompt) ? "Giải thích tổng quan dự báo BTC hiện tại" : request.Prompt.Trim();

        var context = await _contextService.GetFullMarketContextAsync(symbol, timeframe, ct);

        try
        {
            // Try forwarding to FastAPI Python AI Service /api/explain if available
            var client = _httpClientFactory.CreateClient("AIService");
            var payload = new
            {
                prompt = userQuestion,
                market_context = context
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/explain", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(ct);
                var pyResult = JsonSerializer.Deserialize<JsonElement>(responseJson);

                return Ok(new
                {
                    prompt = userQuestion,
                    answer = pyResult.TryGetProperty("answer", out var ans) ? ans.GetString() : "Đã nhận phản hồi từ AI.",
                    evidenceTags = pyResult.TryGetProperty("evidence_tags", out var ev) ? ev : (object)new[] { "Archetype", "Regime", "VPVR", "SMC", "Confluence" },
                    timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python AI Service /api/explain unavailable, using C# Structured Explainer fallback");
        }

        // C# Fallback Explainer using full context
        string fallbackAnswer = GenerateStructuredExplanation(userQuestion, context);

        return Ok(new
        {
            prompt = userQuestion,
            answer = fallbackAnswer,
            evidenceTags = new[] { "Archetype Markov", "Market Regime", "Multi-TF Confluence", "VPVR & SMC", "Master Ensemble" },
            timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    [HttpPost("stream")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    public async Task StreamAi([FromBody] AiChatQueryDto request, CancellationToken ct)
    {
        string symbol = request.Symbol ?? "BTCUSDT";
        string timeframe = request.Timeframe ?? "1h";
        string userQuestion = string.IsNullOrWhiteSpace(request.Prompt) ? "Giải thích tổng quan dự báo BTC hiện tại" : request.Prompt.Trim();

        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var context = await _contextService.GetFullMarketContextAsync(symbol, timeframe, ct);
        bool streamedFromAiService = false;

        try
        {
            var client = _httpClientFactory.CreateClient("AIService");
            var payload = new
            {
                prompt = userQuestion,
                market_context = context
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/explain/stream") { Content = content };
            using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line != null)
                    {
                        await Response.WriteAsync($"{line}\n", ct);
                        await Response.Body.FlushAsync(ct);
                        if (line.StartsWith("data:")) streamedFromAiService = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python AI Service streaming failed, falling back to C# structured stream");
        }

        if (!streamedFromAiService)
        {
            // C# structured streaming fallback
            string fallbackAnswer = GenerateStructuredExplanation(userQuestion, context);
            var words = fallbackAnswer.Split(new[] { ' ' }, StringSplitOptions.None);
            for (int i = 0; i < words.Length; i++)
            {
                if (ct.IsCancellationRequested) break;
                string chunk = words[i] + (i < words.Length - 1 ? " " : "");
                var tokenPayload = JsonSerializer.Serialize(new { token = chunk, done = false });
                await Response.WriteAsync($"data: {tokenPayload}\n\n", ct);
                await Response.Body.FlushAsync(ct);
                await Task.Delay(15, ct); // smooth typewriter pacing
            }

            var finalPayload = JsonSerializer.Serialize(new
            {
                token = "",
                done = true,
                evidence_tags = new[] { "Archetype Markov", "Market Regime", "Multi-TF Confluence", "VPVR & SMC", "Master Ensemble" }
            });
            await Response.WriteAsync($"data: {finalPayload}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    private static string GenerateStructuredExplanation(string prompt, FullMarketContextDto ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### 🤖 Phân Tích & Giải Thích AI Tự Động ({ctx.Symbol} - {ctx.Timeframe})");
        sb.AppendLine($"**Giá hiện tại**: `${ctx.CurrentPrice:N2}`\n");

        sb.AppendLine("#### 1. Master Ensemble AI Forecast");
        sb.AppendLine($"Dự báo tổng hợp 5 lớp: **{ctx.MasterEnsemblePrediction}**");
        sb.AppendLine();

        sb.AppendLine("#### 2. Hội Tụ Đa Khung Thời Gian (Confluence)");
        sb.AppendLine($"Chỉ số Confluence: **{ctx.MultiTimeframeConfluence}**");
        sb.AppendLine();

        sb.AppendLine("#### 3. Chế Độ Thị Trường (Market Regime)");
        sb.AppendLine($"Trạng thái thị trường: **{ctx.MarketRegime}**");
        sb.AppendLine();

        sb.AppendLine("#### 4. Khối Lượng VPVR & Cấu Trúc Smart Money (SMC)");
        sb.AppendLine($"Vùng giá POC / VAH / VAL: **{ctx.VolumeProfile}**");
        sb.AppendLine($"Cấu trúc nến SMC (BOS/CHoCH/FVG): **{ctx.SmartMoneyStructures}**");
        sb.AppendLine();

        sb.AppendLine("#### 5. Mẫu Nến Archetype & Xác Suất Markov");
        sb.AppendLine($"Mẫu nến cửa sổ hiện tại: **{ctx.ArchetypeMatch}**");
        sb.AppendLine($"Xác suất chuyển đổi tiếp theo: **{ctx.MarkovTransitions}**");

        return sb.ToString();
    }
}

public class AiChatQueryDto
{
    public string? Symbol { get; set; }
    public string? Timeframe { get; set; }
    public string? Prompt { get; set; }
}
