using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class AiContextService : IAiContextService
{
    private readonly AppDbContext _db;
    private readonly IArchetypeService _archetypeService;
    private readonly ITransitionService _transitionService;
    private readonly IRegimeDetectionService _regimeService;
    private readonly IConfluenceService _confluenceService;
    private readonly IVolumeProfileService _volumeProfileService;
    private readonly ISmartMoneyService _smartMoneyService;
    private readonly ISentimentService _sentimentService;
    private readonly IEnsembleService _ensembleService;
    private readonly IBinanceKlinesService _binance;
    private readonly ILogger<AiContextService> _logger;

    public AiContextService(
        AppDbContext db,
        IArchetypeService archetypeService,
        ITransitionService transitionService,
        IRegimeDetectionService regimeService,
        IConfluenceService confluenceService,
        IVolumeProfileService volumeProfileService,
        ISmartMoneyService smartMoneyService,
        ISentimentService sentimentService,
        IEnsembleService ensembleService,
        IBinanceKlinesService binance,
        ILogger<AiContextService> logger)
    {
        _db = db;
        _archetypeService = archetypeService;
        _transitionService = transitionService;
        _regimeService = regimeService;
        _confluenceService = confluenceService;
        _volumeProfileService = volumeProfileService;
        _smartMoneyService = smartMoneyService;
        _sentimentService = sentimentService;
        _ensembleService = ensembleService;
        _binance = binance;
        _logger = logger;
    }

    public async Task<FullMarketContextDto> GetFullMarketContextAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default)
    {
        var klines = await _binance.GetKlinesAsync(symbol, timeframe, 2, cancellationToken: ct);
        double currentPrice = klines.Count > 0 ? (double)klines[^1].Close : 65000.0;
        long timeMs = klines.Count > 0 ? klines[^1].OpenTimeMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1. Archetype Match
        object? archetypeMatch = null;
        try
        {
            archetypeMatch = await _archetypeService.MatchCurrentWindowAsync(symbol, timeframe, 10, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch archetype match"); }

        // 2. Markov Transitions
        object? markovTransitions = null;
        try
        {
            markovTransitions = await _transitionService.PredictNextAsync(symbol, timeframe, 10, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch markov transitions"); }

        // 3. Market Regime
        object? regime = null;
        try
        {
            regime = await _regimeService.GetCurrentRegimeAsync(symbol, timeframe, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch market regime"); }

        // 4. Confluence
        object? confluence = null;
        try
        {
            confluence = await _confluenceService.GetLatestConfluenceAsync(symbol, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch confluence"); }

        // 5. Volume Profile
        object? volumeProfile = null;
        try
        {
            volumeProfile = await _volumeProfileService.GetVolumeProfileAsync(symbol, timeframe, 200, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch volume profile"); }

        // 6. Smart Money Concepts
        object? smc = null;
        try
        {
            smc = await _smartMoneyService.GetSmartMoneyStructuresAsync(symbol, timeframe, 200, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch smart money concepts"); }

        // 7. Sentiment & News
        object? sentiment = null;
        try
        {
            var sentSnapshot = await _sentimentService.GetLatestSentimentAsync(symbol, ct);
            var recentNews = await _db.NewsArticles.AsNoTracking()
                .OrderByDescending(n => n.PublishedAt ?? n.FetchedAt)
                .Take(5)
                .Select(n => new { n.Title, n.Source, n.Summary })
                .ToListAsync(ct);

            sentiment = new
            {
                Snapshot = sentSnapshot,
                NewsHeadlines = recentNews
            };
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch sentiment"); }

        // 8. Ensemble Forecast & Paper Trade
        object? masterPrediction = null;
        try
        {
            masterPrediction = await _ensembleService.PredictEnsembleAsync(symbol, timeframe, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch ensemble prediction"); }

        object? activePaperTrade = null;
        try
        {
            activePaperTrade = await _db.PaperTrades.AsNoTracking()
                .Where(p => p.Symbol == symbol && (p.Status == "open" || p.Status == "OPEN"))
                .OrderByDescending(p => p.EntryTimeMs)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch active paper trade"); }

        return new FullMarketContextDto
        {
            Symbol = symbol,
            Timeframe = timeframe,
            CurrentPrice = currentPrice,
            ContextTimeMs = timeMs,
            ArchetypeMatch = archetypeMatch,
            MarkovTransitions = markovTransitions,
            MarketRegime = regime,
            MultiTimeframeConfluence = confluence,
            VolumeProfile = volumeProfile,
            SmartMoneyStructures = smc,
            SentimentAndNews = sentiment,
            MasterEnsemblePrediction = masterPrediction,
            ActivePaperTrade = activePaperTrade
        };
    }
}
