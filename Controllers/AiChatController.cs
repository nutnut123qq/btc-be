using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/ai-chat")]
public class AiChatController : ControllerBase
{
    internal const int MaxBufferedSseChars = 65_536;
    private static readonly string[] DeterministicEvidenceTags =
        ["Experimental: Archetype Markov", "Market Regime", "Multi-TF Confluence", "VPVR & SMC", "Experimental: Ensemble"];
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

    [HttpGet("capabilities")]
    public async Task<ActionResult<AiCapabilitiesDto>> GetCapabilities(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var client = _httpClientFactory.CreateClient("AIService");
            using var response = await client.GetAsync("/api/capabilities", timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<AiCapabilitiesDto>(cancellationToken: timeout.Token);
                if (result != null)
                {
                    result.FallbackExplanation = true;
                    return Ok(result);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation("AI capabilities unavailable; deterministic explanation fallback remains active");
        }

        return Ok(AiCapabilitiesDto.Unavailable("AI service is unavailable."));
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

                var answer = pyResult.TryGetProperty("answer", out var ans) ? ans.GetString() : null;
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    return Ok(new
                    {
                        prompt = userQuestion,
                        answer,
                        evidenceTags = ReadEvidenceTags(pyResult),
                        timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
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
            evidenceTags = DeterministicEvidenceTags,
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
        var upstreamTokens = new StringBuilder();
        string[]? upstreamEvidenceTags = null;
        bool upstreamCompleted = false;
        bool upstreamFailed = false;

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
                    if (line?.StartsWith("data:", StringComparison.Ordinal) == true)
                    {
                        var dataPayload = line[5..].Trim();
                        if (string.IsNullOrEmpty(dataPayload)) continue;
                        try
                        {
                            using var document = JsonDocument.Parse(dataPayload);
                            var root = document.RootElement;
                            if (root.TryGetProperty("error", out _))
                            {
                                upstreamFailed = true;
                                break;
                            }
                            if (root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String)
                            {
                                var value = token.GetString() ?? "";
                                if (upstreamTokens.Length + value.Length > MaxBufferedSseChars)
                                {
                                    upstreamFailed = true;
                                    break;
                                }
                                upstreamTokens.Append(value);
                            }
                            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                            {
                                upstreamCompleted = true;
                                upstreamEvidenceTags = ReadEvidenceTags(root);
                                break;
                            }
                        }
                        catch (JsonException)
                        {
                            upstreamFailed = true;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python AI Service streaming failed, falling back to C# structured stream");
        }

        if (upstreamCompleted && !upstreamFailed && upstreamTokens.Length > 0)
        {
            await WriteStreamAsync(upstreamTokens.ToString(), upstreamEvidenceTags ?? [], ct);
        }
        else
        {
            await WriteStreamAsync(
                GenerateStructuredExplanation(userQuestion, context),
                DeterministicEvidenceTags,
                ct);
        }
    }

    private async Task WriteStreamAsync(string answer, string[] evidenceTags, CancellationToken ct)
    {
        var tokenPayload = JsonSerializer.Serialize(new { token = answer, done = false });
        await Response.WriteAsync($"data: {tokenPayload}\n\n", ct);
        var finalPayload = JsonSerializer.Serialize(new { token = "", done = true, evidence_tags = evidenceTags });
        await Response.WriteAsync($"data: {finalPayload}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private static string[] ReadEvidenceTags(JsonElement root)
    {
        if (!root.TryGetProperty("evidence_tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return DeterministicEvidenceTags;
        var result = tags.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Contains("ensemble", StringComparison.OrdinalIgnoreCase)
                ? "Experimental: Ensemble"
                : x.Contains("markov", StringComparison.OrdinalIgnoreCase)
                    ? "Experimental: Archetype Markov"
                    : x)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return result.Length > 0 ? result : DeterministicEvidenceTags;
    }

    internal static string GenerateStructuredExplanation(string prompt, FullMarketContextDto ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Giải thích định lượng tự động ({ctx.Symbol} - {ctx.Timeframe})");
        sb.AppendLine($"**Giá hiện tại**: `${ctx.CurrentPrice:N2}`\n");

        sb.AppendLine("#### 1. Ensemble (Experimental — chưa qua OOS gate)");
        sb.AppendLine($"Dự báo tổng hợp: **{RenderValue(ctx.MasterEnsemblePrediction)}**");
        sb.AppendLine();

        sb.AppendLine("#### 2. Hội Tụ Đa Khung Thời Gian (Confluence)");
        sb.AppendLine($"Chỉ số Confluence: **{RenderValue(ctx.MultiTimeframeConfluence)}**");
        sb.AppendLine();

        sb.AppendLine("#### 3. Chế Độ Thị Trường (Market Regime)");
        sb.AppendLine($"Trạng thái thị trường: **{RenderValue(ctx.MarketRegime)}**");
        sb.AppendLine();

        sb.AppendLine("#### 4. Khối Lượng VPVR & Cấu Trúc Smart Money (SMC)");
        sb.AppendLine($"Vùng giá POC / VAH / VAL: **{RenderValue(ctx.VolumeProfile)}**");
        sb.AppendLine($"Cấu trúc nến SMC (BOS/CHoCH/FVG): **{RenderValue(ctx.SmartMoneyStructures)}**");
        sb.AppendLine();

        sb.AppendLine("#### 5. Archetype & Markov (Experimental — chưa qua OOS gate)");
        sb.AppendLine($"Mẫu nến cửa sổ hiện tại: **{RenderValue(ctx.ArchetypeMatch)}**");
        sb.AppendLine($"Thống kê chuyển đổi: **{RenderValue(ctx.MarkovTransitions)}**");

        return sb.ToString();
    }

    private static string RenderValue(object? value)
    {
        if (value == null) return "Không có dữ liệu";
        if (value is string text) return string.IsNullOrWhiteSpace(text) ? "Không có dữ liệu" : text;
        if (value is JsonElement element) return element.GetRawText();
        try
        {
            return JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "Dữ liệu không thể hiển thị an toàn";
        }
    }
}

public class AiChatQueryDto
{
    public string? Symbol { get; set; }
    public string? Timeframe { get; set; }
    public string? Prompt { get; set; }
}

public sealed class AiCapabilitiesDto
{
    public bool MlInference { get; set; }
    public bool LlmExplanation { get; set; }
    public string Provider { get; set; } = "unavailable";
    public string? Reason { get; set; }
    public bool FallbackExplanation { get; set; } = true;

    public static AiCapabilitiesDto Unavailable(string reason) => new()
    {
        MlInference = false,
        LlmExplanation = false,
        Provider = "unavailable",
        Reason = reason,
        FallbackExplanation = true
    };
}
