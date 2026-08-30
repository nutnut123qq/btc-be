using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.Controllers;

[ApiController]
[Route("api/paper-trades")]
public class PaperTradeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEnsemblePaperTraderService _ensemblePaperTraderService;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan SummaryTtl = TimeSpan.FromSeconds(5);

    [ActivatorUtilitiesConstructor]
    public PaperTradeController(
        AppDbContext db,
        IEnsemblePaperTraderService ensemblePaperTraderService,
        IMemoryCache cache)
    {
        _db = db;
        _ensemblePaperTraderService = ensemblePaperTraderService;
        _cache = cache;
    }

    public PaperTradeController(
        AppDbContext db,
        IEnsemblePaperTraderService ensemblePaperTraderService)
        : this(db, ensemblePaperTraderService, new MemoryCache(new MemoryCacheOptions()))
    {
    }

    [HttpPost("evaluate-ensemble")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> EvaluateEnsemble([FromBody] EvaluateEnsembleRequest request, CancellationToken ct)
    {
        var result = await _ensemblePaperTraderService.EvaluateAndTradeAsync(request.Symbol, request.Timeframe, ct);
        return Ok(result);
    }

    /// <summary>
    /// Lấy danh sách giao dịch với bộ lọc nâng cao (hỗ trợ nhiều symbols, phân trang, lọc theo side, status, thời gian).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] string? symbols = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string? timeframe = null,
        [FromQuery] string? status = null,
        [FromQuery] string? side = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] int? take = null)
    {
        int effectivePageSize = take.HasValue ? Math.Clamp(take.Value, 1, 200) : Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var query = _db.PaperTrades.AsNoTracking();

        // 1. Filter by symbol(s)
        string? targetSymbols = !string.IsNullOrWhiteSpace(symbols) ? symbols : symbol;
        if (!string.IsNullOrWhiteSpace(targetSymbols) && targetSymbols.Trim().ToLower() != "all")
        {
            var symbolList = targetSymbols
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .ToList();

            if (symbolList.Count == 1)
            {
                var s = symbolList[0];
                query = query.Where(t => t.Symbol == s);
            }
            else if (symbolList.Count > 1)
            {
                query = query.Where(t => symbolList.Contains(t.Symbol.ToUpper()));
            }
        }

        // 2. Filter by timeframe
        if (!string.IsNullOrWhiteSpace(timeframe) && timeframe.Trim().ToLower() != "all")
        {
            query = query.Where(t => t.Timeframe == timeframe);
        }

        // 3. Filter by status
        if (!string.IsNullOrWhiteSpace(status) && status.Trim().ToLower() != "all")
        {
            query = query.Where(t => t.Status.ToLower() == status.Trim().ToLower());
        }

        // 4. Filter by side
        if (!string.IsNullOrWhiteSpace(side) && side.Trim().ToLower() != "all")
        {
            query = query.Where(t => t.Side.ToLower() == side.Trim().ToLower());
        }

        // 5. Filter by time range
        if (fromDate.HasValue)
        {
            long fromMs = new DateTimeOffset(fromDate.Value.ToUniversalTime()).ToUnixTimeMilliseconds();
            query = query.Where(t => t.EntryTimeMs >= fromMs);
        }

        if (toDate.HasValue)
        {
            long toMs = new DateTimeOffset(toDate.Value.ToUniversalTime()).ToUnixTimeMilliseconds();
            query = query.Where(t => t.EntryTimeMs <= toMs);
        }

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling((double)totalCount / effectivePageSize);

        var rawItems = await query
            .OrderByDescending(t => t.EntryTimeMs)
            .Skip((page - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync();

        var items = rawItems.Select(t =>
        {
            double posSize = t.PositionSizeUsdt ?? 2000.0;
            double entryP = t.EntryPrice ?? 1.0;
            double executedQty = entryP > 0 ? posSize / entryP : 0.0;
            double? netRet = PaperTradeMetrics.NetReturn(t);
            double? realizedPnL = PaperTradeMetrics.RealizedPnlUsdt(t);
            double? netRetPct = netRet.HasValue ? netRet.Value * 100.0 : null;

            return new
            {
                t.Id,
                t.Symbol,
                t.Timeframe,
                t.Side,
                t.Confidence,
                t.ProbDown,
                t.ProbSideways,
                t.ProbUp,
                t.EntryPrice,
                t.ExitPrice,
                PositionSizeUsdt = posSize,
                ExecutedQty = Math.Round(executedQty, 6),
                t.TakeProfitPrice,
                t.StopLossPrice,
                t.Atr14,
                t.ExitReason,
                NetReturn = netRet,
                NetReturnPct = netRetPct.HasValue ? Math.Round(netRetPct.Value, 2) : (double?)null,
                RealizedPnLUsdt = realizedPnL.HasValue ? Math.Round(realizedPnL.Value, 2) : (double?)null,
                t.BalanceAfter,
                t.Status,
                t.ModelVersion,
                t.EnsembleDirection,
                t.WindowEndMs,
                t.EntryTimeMs,
                t.ExitTimeMs,
                t.CreatedAtUtc,
                t.ClosedAtUtc
            };
        });

        return Ok(new
        {
            totalCount,
            page,
            pageSize = effectivePageSize,
            totalPages,
            items
        });
    }

    /// <summary>
    /// API Tổng quan danh mục Đa tài sản (Multi-Asset Portfolio Summary) chuẩn Binance.
    /// </summary>
    [HttpGet("portfolio-summary")]
    public async Task<IActionResult> GetPortfolioSummary(
        [FromQuery] double initialBalance = 10000.0)
    {
        var cacheKey = $"paper:portfolio-summary:{initialBalance}";
        if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
        {
            return Ok(cached);
        }

        var allTrades = await _db.PaperTrades.AsNoTracking().ToListAsync();

        var totalTrades = allTrades.Count;
        var openTrades = allTrades.Where(t => t.Status == "open").ToList();
        var closedTrades = allTrades.Where(t => t.Status == "closed").ToList();

        var closedWithReturns = closedTrades
            .Select(t => new { Trade = t, NetReturn = PaperTradeMetrics.NetReturn(t) })
            .Where(x => x.NetReturn.HasValue)
            .ToList();
        var winCount = closedWithReturns.Count(x => x.NetReturn > 0);
        var lossCount = closedWithReturns.Count - winCount;
        var winRatePct = closedWithReturns.Count > 0
            ? Math.Round((double)winCount / closedWithReturns.Count * 100.0, 1)
            : 0.0;

        double totalRealizedPnLUsdt = 0.0;
        foreach (var t in closedTrades)
            totalRealizedPnLUsdt += PaperTradeMetrics.RealizedPnlUsdt(t) ?? 0;

        double currentBalance = initialBalance + totalRealizedPnLUsdt;
        double totalRealizedPnLPct = initialBalance > 0 ? (totalRealizedPnLUsdt / initialBalance) * 100.0 : 0.0;

        // Breakdown by Symbol
        var breakdownBySymbol = allTrades
            .GroupBy(t => t.Symbol)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var closed = g.Where(x => x.Status == "closed").ToList();
                    var returns = closed.Select(PaperTradeMetrics.NetReturn).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                    var wins = returns.Count(x => x > 0);
                    var losses = returns.Count - wins;
                    var wr = returns.Count > 0 ? Math.Round((double)wins / returns.Count * 100.0, 1) : 0.0;

                    double symPnLUsdt = 0.0;
                    foreach (var ct in closed)
                        symPnLUsdt += PaperTradeMetrics.RealizedPnlUsdt(ct) ?? 0;

                    double avgRetPct = returns.Count > 0 ? returns.Average() * 100.0 : 0.0;
                    double bestRetPct = returns.Count > 0 ? returns.Max() * 100.0 : 0.0;
                    double worstRetPct = returns.Count > 0 ? returns.Min() * 100.0 : 0.0;

                    return new
                    {
                        Symbol = g.Key,
                        TotalTrades = g.Count(),
                        OpenTrades = g.Count(x => x.Status == "open"),
                        ClosedTrades = closed.Count,
                        WinCount = wins,
                        LossCount = losses,
                        WinRatePct = wr,
                        RealizedPnLUsdt = Math.Round(symPnLUsdt, 2),
                        AvgReturnPct = Math.Round(avgRetPct, 2),
                        BestTradePct = Math.Round(bestRetPct, 2),
                        WorstTradePct = Math.Round(worstRetPct, 2)
                    };
                }
            );

        var result = new
        {
            InitialBalance = initialBalance,
            CurrentBalance = Math.Round(currentBalance, 2),
            RealizedPnLUsdt = Math.Round(totalRealizedPnLUsdt, 2),
            RealizedPnLPct = Math.Round(totalRealizedPnLPct, 2),
            TotalTrades = totalTrades,
            OpenTrades = openTrades.Count,
            ClosedTrades = closedTrades.Count,
            InvalidClosedTrades = closedTrades.Count - closedWithReturns.Count,
            WinCount = winCount,
            LossCount = lossCount,
            WinRatePct = winRatePct,
            BreakdownBySymbol = breakdownBySymbol
        };

        _cache.Set(cacheKey, result, SummaryTtl);
        return Ok(result);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? symbol = null,
        [FromQuery] string? timeframe = null)
    {
        var cacheKey = $"paper:summary:{symbol ?? "all"}:{timeframe}";
        if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
        {
            return Ok(cached);
        }

        var query = _db.PaperTrades.AsNoTracking();
        if (!string.IsNullOrEmpty(symbol))
            query = query.Where(t => t.Symbol == symbol);

        if (!string.IsNullOrEmpty(timeframe))
            query = query.Where(t => t.Timeframe == timeframe);

        var allTrades = await query.ToListAsync();

        var totalTrades = allTrades.Count;
        var openTrades = allTrades.Count(t => t.Status == "open");
        var closedTrades = allTrades.Where(t => t.Status == "closed").ToList();
        
        var returns = closedTrades.Select(PaperTradeMetrics.NetReturn).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var winCount = returns.Count(t => t > 0);
        var winRate = returns.Count > 0 ? (double)winCount / returns.Count : 0;

        var totalNetReturnPct = returns.Sum() * 100;
        var avgReturnPct = returns.Count > 0 ? returns.Average() * 100 : 0;

        var bestTradePct = returns.Count > 0 ? returns.Max() * 100 : 0;
        var worstTradePct = returns.Count > 0 ? returns.Min() * 100 : 0;

        var longCount = allTrades.Count(t => string.Equals(t.Side, "long", StringComparison.OrdinalIgnoreCase));
        var shortCount = allTrades.Count(t => string.Equals(t.Side, "short", StringComparison.OrdinalIgnoreCase));

        double maxDrawdownPct = 0;
        double peak = 1;
        double currentEquity = 1;

        foreach (var netReturn in closedTrades.OrderBy(t => t.ExitTimeMs).Select(PaperTradeMetrics.NetReturn))
        {
            if (!netReturn.HasValue) continue;
            currentEquity *= 1 + netReturn.Value;
            if (currentEquity > peak)
            {
                peak = currentEquity;
            }
            var drawdown = (peak - currentEquity) / peak * 100;
            if (drawdown > maxDrawdownPct)
            {
                maxDrawdownPct = drawdown;
            }
        }

        var result = new
        {
            totalTrades,
            openTrades,
            closedTrades = closedTrades.Count,
            invalidClosedTrades = closedTrades.Count - returns.Count,
            winRate,
            totalNetReturnPct,
            avgReturnPct,
            maxDrawdownPct,
            bestTradePct,
            worstTradePct,
            longCount,
            shortCount
        };

        _cache.Set(cacheKey, result, SummaryTtl);
        return Ok(result);
    }

    [HttpGet("equity-curve")]
    public async Task<IActionResult> GetEquityCurve(
        [FromQuery] string? symbol = null,
        [FromQuery] string? timeframe = null)
    {
        var cacheKey = $"paper:equity-curve:{symbol ?? "all"}:{timeframe}";
        if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
        {
            return Ok(cached);
        }

        var query = _db.PaperTrades.AsNoTracking().Where(t => t.Status == "closed");
        if (!string.IsNullOrEmpty(symbol))
            query = query.Where(t => t.Symbol == symbol);

        if (!string.IsNullOrEmpty(timeframe))
            query = query.Where(t => t.Timeframe == timeframe);

        var closedTrades = await query.OrderBy(t => t.ExitTimeMs).ToListAsync();

        var result = new List<object>();
        double cumulativeProduct = 1;
        int tradeCount = 0;

        foreach (var t in closedTrades)
        {
            var netReturn = PaperTradeMetrics.NetReturn(t);
            if (!netReturn.HasValue) continue;
            cumulativeProduct *= 1 + netReturn.Value;
            tradeCount++;
            result.Add(new
            {
                timeMs = t.ExitTimeMs,
                cumulativeReturnPct = (cumulativeProduct - 1) * 100,
                tradeCount
            });
        }

        var response = new { symbol = symbol ?? "all", points = result };
        _cache.Set(cacheKey, response, SummaryTtl);
        return Ok(response);
    }

    [HttpGet("open")]
    public async Task<IActionResult> GetOpen(
        [FromQuery] string? symbol = null)
    {
        var query = _db.PaperTrades.AsNoTracking().Where(t => t.Status == "open");
        if (!string.IsNullOrEmpty(symbol))
            query = query.Where(t => t.Symbol == symbol);

        var items = await query
            .OrderByDescending(t => t.EntryTimeMs)
            .ToListAsync();

        return Ok(new { symbol = symbol ?? "all", count = items.Count, items });
    }
}

public class EvaluateEnsembleRequest { public string Symbol { get; set; } = "BTCUSDT"; public string Timeframe { get; set; } = "1h"; }
