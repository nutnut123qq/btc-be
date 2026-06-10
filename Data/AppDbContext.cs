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
            e.Property(x => x.UserId).HasMaxLength(128);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Message).HasMaxLength(4000);
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
    }
}
