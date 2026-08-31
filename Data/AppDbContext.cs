using Microsoft.EntityFrameworkCore;

namespace Backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<NewsArticle> NewsArticles => Set<NewsArticle>();
    public DbSet<NewsChunk> NewsChunks => Set<NewsChunk>();
    public DbSet<AppAlert> AppAlerts => Set<AppAlert>();
    public DbSet<PriceAlertSettings> PriceAlertSettings => Set<PriceAlertSettings>();
    public DbSet<WindowVector> WindowVectors => Set<WindowVector>();
    public DbSet<CandlePattern> CandlePatterns => Set<CandlePattern>();
    public DbSet<CandleSequenceRule> CandleSequenceRules => Set<CandleSequenceRule>();
    public DbSet<CandleSequenceSignal> CandleSequenceSignals => Set<CandleSequenceSignal>();
    public DbSet<CandleVolumeStats> CandleVolumeStats => Set<CandleVolumeStats>();
    public DbSet<Kline> Klines => Set<Kline>();
    public DbSet<TechnicalIndicator> TechnicalIndicators => Set<TechnicalIndicator>();
    public DbSet<MarketMetrics> MarketMetrics => Set<MarketMetrics>();
    public DbSet<MlFeatureStore> MlFeatureStores => Set<MlFeatureStore>();
    public DbSet<PriceTarget> PriceTargets => Set<PriceTarget>();
    public DbSet<PatternSequence> PatternSequences => Set<PatternSequence>();
    public DbSet<WindowClassificationDataset> WindowClassificationDatasets => Set<WindowClassificationDataset>();
    public DbSet<ModelPrediction> ModelPredictions => Set<ModelPrediction>();
    public DbSet<BacktestRun> BacktestRuns => Set<BacktestRun>();
    public DbSet<BacktestTrade> BacktestTrades => Set<BacktestTrade>();
    public DbSet<PaperTrade> PaperTrades => Set<PaperTrade>();
    public DbSet<CandleArchetype> CandleArchetypes => Set<CandleArchetype>();
    public DbSet<ArchetypeOutcome> ArchetypeOutcomes => Set<ArchetypeOutcome>();
    public DbSet<ArchetypeOccurrence> ArchetypeOccurrences => Set<ArchetypeOccurrence>();
    public DbSet<ArchetypeTransition> ArchetypeTransitions => Set<ArchetypeTransition>();
    public DbSet<ArchetypeSequence> ArchetypeSequences => Set<ArchetypeSequence>();
    public DbSet<MarketRegime> MarketRegimes => Set<MarketRegime>();
    public DbSet<RegimeTransition> RegimeTransitions => Set<RegimeTransition>();
    public DbSet<ConfluenceSnapshot> ConfluenceSnapshots => Set<ConfluenceSnapshot>();
    public DbSet<VolumeProfileSnapshot> VolumeProfileSnapshots => Set<VolumeProfileSnapshot>();
    public DbSet<SmartMoneyStructure> SmartMoneyStructures => Set<SmartMoneyStructure>();
    public DbSet<SentimentSnapshot> SentimentSnapshots => Set<SentimentSnapshot>();
    public DbSet<EnsemblePredictionRecord> EnsemblePredictionRecords => Set<EnsemblePredictionRecord>();
    public DbSet<FuturesMetric> FuturesMetrics => Set<FuturesMetric>();
    public DbSet<LiquidationSnapshot> LiquidationSnapshots => Set<LiquidationSnapshot>();
    public DbSet<WalletBalanceSnapshot> WalletBalanceSnapshots => Set<WalletBalanceSnapshot>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NewsArticle>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Link).IsUnique();
            e.Property(x => x.Title).HasMaxLength(2000);
            e.Property(x => x.Link).HasMaxLength(4000);
            e.Property(x => x.Source).HasMaxLength(256);
        });

        modelBuilder.Entity<NewsChunk>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ArticleId);
            e.Property(x => x.Text).HasMaxLength(16000);
            e.Property(x => x.Embedding).HasColumnType("real[]");

            e.HasOne(x => x.Article)
                .WithMany(a => a.Chunks)
                .HasForeignKey(x => x.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppAlert>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.Type, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.SourceKey })
                .IsUnique()
                .HasFilter("\"SourceKey\" IS NOT NULL AND \"ArchivedAtUtc\" IS NULL");
            e.Property(x => x.UserId).HasMaxLength(128);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Message).HasMaxLength(4000);
            e.Property(x => x.SourceKey).HasMaxLength(512);
        });

        modelBuilder.Entity<PriceAlertSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(128);
            e.Property(x => x.KlineInterval).HasMaxLength(16);
            // Explicitly match the table name used in EF migration InsertData
            // (prevents "no entity type mapped to the table" at migration runtime).
            e.ToTable("PriceAlertSettings");
        });

        modelBuilder.Entity<WindowVector>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.FeatureType).HasMaxLength(16);
            e.Property(x => x.Vector).HasColumnType("real[]");
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.FeatureType, x.WindowSize, x.StartTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.FeatureType, x.WindowSize, x.EndTimeMs });
        });

        modelBuilder.Entity<CandlePattern>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.PatternType).HasMaxLength(64);
            e.Property(x => x.PatternCategory).HasMaxLength(16);
            e.Property(x => x.TrendDirection).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs, x.PatternType }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.PatternType });
        });

        modelBuilder.Entity<CandleSequenceRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Action).HasMaxLength(32);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsEnabled });
        });

        modelBuilder.Entity<CandleSequenceSignal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.RuleId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CreatedAtUtc });
        });

        modelBuilder.Entity<CandleVolumeStats>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.VolumeTrend).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.VolumeAnomalyRatio });
        });

        modelBuilder.Entity<Kline>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CloseTimeMs });
        });

        modelBuilder.Entity<TechnicalIndicator>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
        });

        modelBuilder.Entity<MarketMetrics>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
        });

        modelBuilder.Entity<MlFeatureStore>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CreatedAtUtc });
        });

        modelBuilder.Entity<PriceTarget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
        });

        modelBuilder.Entity<PatternSequence>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.PatternChainJson).HasMaxLength(4000);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.StartTimeMs });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize });
        });

        modelBuilder.Entity<WindowClassificationDataset>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Horizon).HasMaxLength(16);
            e.Property(x => x.FeatureVector).HasColumnType("real[]");
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize, x.Horizon, x.WindowStartMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize, x.Horizon, x.Label });
        });

        modelBuilder.Entity<ModelPrediction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Horizon).HasMaxLength(16);
            e.Property(x => x.ModelVersion).HasMaxLength(256);
            e.Property(x => x.PipelineVersion).HasMaxLength(128);
            e.Property(x => x.EvaluationVersion).HasMaxLength(128);
            e.Property(x => x.ValidityStatus).HasMaxLength(16);
            e.Property(x => x.InvalidReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize, x.Horizon, x.WindowEndMs });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CreatedAtUtc });
            e.HasIndex(x => new { x.ValidityStatus, x.ArchivedAtUtc });
        });

        modelBuilder.Entity<BacktestRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Horizon).HasMaxLength(16);
            e.Property(x => x.ModelName).HasMaxLength(256);
            e.Property(x => x.PipelineVersion).HasMaxLength(128);
            e.Property(x => x.EvaluationVersion).HasMaxLength(128);
            e.Property(x => x.ValidityStatus).HasMaxLength(16);
            e.Property(x => x.InvalidReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CreatedAtUtc });
            e.HasIndex(x => new { x.ValidityStatus, x.ArchivedAtUtc });
        });

        modelBuilder.Entity<BacktestTrade>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Side).HasMaxLength(16);
            e.HasIndex(x => new { x.BacktestRunId, x.EntryTimeMs });
            e.HasOne(x => x.BacktestRun)
                .WithMany(r => r.Trades)
                .HasForeignKey(x => x.BacktestRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PaperTrade>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Side).HasMaxLength(8);
            e.Property(x => x.Status).HasMaxLength(8);
            e.Property(x => x.ModelVersion).HasMaxLength(128);
            e.Property(x => x.ExitReason).HasMaxLength(16);
            e.Property(x => x.EnsembleDirection).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowEndMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Status });
            e.HasIndex(x => new { x.Symbol, x.EntryTimeMs });
            e.ToTable("PaperTrades");
        });

        modelBuilder.Entity<FuturesMetric>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.HasIndex(x => new { x.Symbol, x.OpenTimeMs }).IsUnique();
            e.ToTable("FuturesMetrics");
        });

        modelBuilder.Entity<CandleArchetype>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.ArchetypeCode).HasMaxLength(32);
            e.Property(x => x.CentroidVector).HasColumnType("real[]");
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize, x.ArchetypeCode, x.Version }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize, x.Version });
        });

        modelBuilder.Entity<ArchetypeOutcome>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Horizon).HasMaxLength(16);
            e.HasIndex(x => new { x.ArchetypeId, x.Horizon });
            e.HasOne(x => x.Archetype)
                .WithMany()
                .HasForeignKey(x => x.ArchetypeId);
        });

        modelBuilder.Entity<ArchetypeOccurrence>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Horizon).HasMaxLength(16);
            e.HasIndex(x => new { x.ArchetypeId, x.Horizon });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize, x.WindowStartMs });
            e.HasOne(x => x.Archetype)
                .WithMany()
                .HasForeignKey(x => x.ArchetypeId);
        });
        modelBuilder.Entity<ArchetypeTransition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.FromArchetypeId, x.ToArchetypeId, x.Symbol, x.Timeframe, x.WindowSize }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize });
            e.HasOne(x => x.FromArchetype)
                .WithMany()
                .HasForeignKey(x => x.FromArchetypeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToArchetype)
                .WithMany()
                .HasForeignKey(x => x.ToArchetypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ArchetypeSequence>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.FirstArchetypeId, x.SecondArchetypeId, x.ThirdArchetypeId, x.Symbol, x.Timeframe, x.WindowSize }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowSize });
            e.HasOne(x => x.FirstArchetype)
                .WithMany()
                .HasForeignKey(x => x.FirstArchetypeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SecondArchetype)
                .WithMany()
                .HasForeignKey(x => x.SecondArchetypeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ThirdArchetype)
                .WithMany()
                .HasForeignKey(x => x.ThirdArchetypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MarketRegime>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.RegimeType });
        });

        modelBuilder.Entity<RegimeTransition>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.TransitionTimeMs });
        });

        modelBuilder.Entity<ConfluenceSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Symbol, x.TimeMs });
        });

        modelBuilder.Entity<VolumeProfileSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.WindowEndMs });
        });

        modelBuilder.Entity<SmartMoneyStructure>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.TimeMs });
        });
        modelBuilder.Entity<SentimentSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Symbol, x.TimeMs });
        });

        modelBuilder.Entity<EnsemblePredictionRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PipelineVersion).HasMaxLength(128);
            e.Property(x => x.EvaluationVersion).HasMaxLength(128);
            e.Property(x => x.ValidityStatus).HasMaxLength(16);
            e.Property(x => x.InvalidReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.TimeMs });
            e.HasIndex(x => new { x.ValidityStatus, x.ArchivedAtUtc });
            e.HasIndex(x => new { x.SourcePredictionId, x.EvaluationVersion })
                .IsUnique()
                .HasFilter("\"SourcePredictionId\" IS NOT NULL");
        });

        modelBuilder.Entity<LiquidationSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.TimestampUtc });
        });

        modelBuilder.Entity<WalletBalanceSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Asset).HasMaxLength(32);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.EventReasonType).HasMaxLength(32);
            e.HasIndex(x => new { x.Asset, x.Timestamp });
            e.HasIndex(x => new { x.Symbol, x.Timestamp });
            e.ToTable("WalletBalanceSnapshots");
        });
    }
}
