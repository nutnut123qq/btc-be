using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/market")]
public class CandleSequenceController : ControllerBase
{
    private readonly IBinanceKlinesService _binance;
    private readonly ILogger<CandleSequenceController> _logger;

    public CandleSequenceController(IBinanceKlinesService binance, ILogger<CandleSequenceController> logger)
    {
        _binance = binance;
        _logger = logger;
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ (integrity) của chuỗi nến OHLC.
    /// </summary>
    [HttpGet("validate-candles")]
    public async Task<IActionResult> ValidateCandles(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string interval = "1h",
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 10, 1000);
        var klines = await _binance.GetKlinesAsync(symbol, interval, limit, cancellationToken: cancellationToken);
        var result = CandleSequenceValidator.Validate(klines, interval);
        return Ok(new
        {
            symbol,
            interval,
            limit,
            result.TotalBars,
            result.ValidBars,
            result.IsValid,
            result.SummaryText,
            issues = result.Issues
        });
    }

    /// <summary>
    /// Phân tích cấu trúc thị trường (Market Structure): Swing High/Low, HH/HL/LH/LL, BOS, CHoCH.
    /// </summary>
    [HttpGet("market-structure")]
    public async Task<IActionResult> MarketStructure(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string interval = "1h",
        [FromQuery] int limit = 200,
        [FromQuery] int swingLookback = 5,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 50, 1000);
        swingLookback = Math.Clamp(swingLookback, 2, 20);

        var klines = await _binance.GetKlinesAsync(symbol, interval, limit, cancellationToken: cancellationToken);
        var result = MarketStructureAnalyzer.Analyze(klines, swingLookback);

        return Ok(new
        {
            symbol,
            interval,
            limit,
            swingLookback,
            result.CurrentTrend,
            result.SummaryText,
            swings = result.Swings.Select(s => new
            {
                s.Swing.Index,
                s.Swing.TimeMs,
                s.Swing.Price,
                type = s.Swing.Type.ToString(),
                label = s.Label.ToString()
            }),
            events = result.Events.Select(e => new
            {
                e.TimeMs,
                type = e.Type.ToString(),
                e.Message,
                e.Price
            })
        });
    }

    /// <summary>
    /// Phân tích kịch bản chuỗi nến (Sequence Scenarios) dựa trên đặc trưng thống kê.
    /// Không dùng tên pattern cổ điển (Morning Star, Engulfing...).
    /// </summary>
    [HttpGet("sequence-scenarios")]
    public async Task<IActionResult> SequenceScenarios(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string interval = "1h",
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 20, 500);
        var klines = await _binance.GetKlinesAsync(symbol, interval, limit, cancellationToken: cancellationToken);
        var result = CandleSequenceScenarios.Analyze(klines, symbol, interval);
        return Ok(result);
    }
}
