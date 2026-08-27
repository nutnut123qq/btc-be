using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExecutionController : ControllerBase
{
    private readonly ILiveOrderExecutionService _executionService;
    private readonly IBinanceUserDataStreamService _streamService;
    private readonly IUserDataStreamHandlerService _handlerService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExecutionController> _logger;
    private static readonly TimeSpan AccountTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StreamStatusTtl = TimeSpan.FromSeconds(1);

    [ActivatorUtilitiesConstructor]
    public ExecutionController(
        ILiveOrderExecutionService executionService,
        IBinanceUserDataStreamService streamService,
        IUserDataStreamHandlerService handlerService,
        IMemoryCache cache,
        ILogger<ExecutionController> logger)
    {
        _executionService = executionService;
        _streamService = streamService;
        _handlerService = handlerService;
        _cache = cache;
        _logger = logger;
    }

    public ExecutionController(
        ILiveOrderExecutionService executionService,
        IBinanceUserDataStreamService streamService,
        IUserDataStreamHandlerService handlerService,
        ILogger<ExecutionController> logger)
        : this(executionService, streamService, handlerService, new MemoryCache(new MemoryCacheOptions()), logger)
    {
    }

    /// <summary>
    /// Lấy thông tin trạng thái kết nối Binance User Data Stream WebSocket & ListenKey
    /// </summary>
    [HttpGet("stream-status")]
    public ActionResult<StreamStatusDto> GetStreamStatus()
    {
        var cacheKey = "exec:stream-status";
        if (_cache.TryGetValue(cacheKey, out StreamStatusDto? cached) && cached != null)
        {
            return Ok(cached);
        }

        var status = _streamService.GetStatus();
        _cache.Set(cacheKey, status, StreamStatusTtl);
        return Ok(status);
    }

    /// <summary>
    /// Kích hoạt kết nối lại thủ công User Data Stream WebSocket
    /// </summary>
    [HttpPost("stream/reconnect")]
    public async Task<IActionResult> ReconnectStream(CancellationToken ct)
    {
        await _streamService.ReconnectAsync(ct);
        return Ok(new { Message = "Reconnection triggered", Status = _streamService.GetStatus() });
    }

    /// <summary>
    /// Lấy danh sách lịch sử snapshot số dư và vị thế từ User Data Stream
    /// </summary>
    [HttpGet("balance-snapshots")]
    public async Task<IActionResult> GetBalanceSnapshots(
        [FromQuery] string asset = "USDT",
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var snapshots = await _handlerService.GetBalanceSnapshotsAsync(asset, limit, ct);
        return Ok(snapshots);
    }

    /// <summary>
    /// Lấy thông tin số dư và trạng thái kết nối Binance Futures Testnet
    /// </summary>
    [HttpGet("account")]
    public async Task<ActionResult<BinanceAccountBalanceResult>> GetAccountBalance(CancellationToken ct)
    {
        var cacheKey = "exec:account";
        if (_cache.TryGetValue(cacheKey, out BinanceAccountBalanceResult? cached) && cached != null)
        {
            return Ok(cached);
        }

        var result = await _executionService.GetAccountBalanceAsync(ct);
        if (result != null && result.Success)
        {
            _cache.Set(cacheKey, result, AccountTtl);
        }
        return Ok(result);
    }

    /// <summary>
    /// Đặt lệnh Market Order lên sàn Binance Futures Testnet
    /// </summary>
    [HttpPost("market-order")]
    public async Task<ActionResult<BinanceOrderResult>> PlaceMarketOrder(
        [FromBody] BinanceOrderRequest request,
        CancellationToken ct)
    {
        if (request.Quantity <= 0)
            return BadRequest(new { Message = "Quantity must be greater than 0" });

        var result = await _executionService.PlaceMarketOrderAsync(
            request.Symbol, request.Side, request.Quantity, ct);
        return Ok(result);
    }

    /// <summary>
    /// Đặt lệnh Stop Loss lên sàn Binance Futures Testnet
    /// </summary>
    [HttpPost("stop-loss")]
    public async Task<ActionResult<BinanceOrderResult>> PlaceStopLossOrder(
        [FromBody] BinanceOrderRequest request,
        CancellationToken ct)
    {
        if (!request.StopPrice.HasValue || request.StopPrice.Value <= 0)
            return BadRequest(new { Message = "StopPrice is required" });

        var result = await _executionService.PlaceStopLossOrderAsync(
            request.Symbol, request.Side, request.StopPrice.Value, request.Quantity, ct);
        return Ok(result);
    }

    /// <summary>
    /// Đặt lệnh Take Profit lên sàn Binance Futures Testnet
    /// </summary>
    [HttpPost("take-profit")]
    public async Task<ActionResult<BinanceOrderResult>> PlaceTakeProfitOrder(
        [FromBody] BinanceOrderRequest request,
        CancellationToken ct)
    {
        if (!request.StopPrice.HasValue || request.StopPrice.Value <= 0)
            return BadRequest(new { Message = "TakeProfit price (StopPrice) is required" });

        var result = await _executionService.PlaceTakeProfitOrderAsync(
            request.Symbol, request.Side, request.StopPrice.Value, request.Quantity, ct);
        return Ok(result);
    }

    /// <summary>
    /// Hủy tất cả các lệnh đang chờ (Open Orders) trên Binance Futures Testnet
    /// </summary>
    [HttpDelete("orders/{symbol}")]
    public async Task<ActionResult<BinanceOrderResult>> CancelAllOrders(
        string symbol,
        CancellationToken ct)
    {
        var result = await _executionService.CancelAllOpenOrdersAsync(symbol, ct);
        return Ok(result);
    }
}
