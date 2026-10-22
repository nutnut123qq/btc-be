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
    public DbSet<RuleDiscoveryRun> RuleDiscoveryRuns => Set<RuleDiscoveryRun>();
    public DbSet<RuleDiscoveryTrial> RuleDiscoveryTrials => Set<RuleDiscoveryTrial>();
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
    public DbSet<TechnicalEvidenceRecord> TechnicalEvidenceRecords => Set<TechnicalEvidenceRecord>();
    public DbSet<TechnicalEvidenceRebuildCheckpoint> TechnicalEvidenceRebuildCheckpoints => Set<TechnicalEvidenceRebuildCheckpoint>();
    public DbSet<SmartMoneyStructure> SmartMoneyStructures => Set<SmartMoneyStructure>();
    public DbSet<CausalSmartMoneyEvent> CausalSmartMoneyEvents => Set<CausalSmartMoneyEvent>();
    public DbSet<CausalSmartMoneyRebuildCheckpoint> CausalSmartMoneyRebuildCheckpoints => Set<CausalSmartMoneyRebuildCheckpoint>();
    public DbSet<SentimentSnapshot> SentimentSnapshots => Set<SentimentSnapshot>();
    public DbSet<EnsemblePredictionRecord> EnsemblePredictionRecords => Set<EnsemblePredictionRecord>();
    public DbSet<FuturesMetric> FuturesMetrics => Set<FuturesMetric>();
    public DbSet<LiquidationSnapshot> LiquidationSnapshots => Set<LiquidationSnapshot>();
    public DbSet<WalletBalanceSnapshot> WalletBalanceSnapshots => Set<WalletBalanceSnapshot>();
    public DbSet<KlineGapState> KlineGapStates => Set<KlineGapState>();
    public DbSet<InstrumentCandle> InstrumentCandles => Set<InstrumentCandle>();
    public DbSet<KlineDataRepairRun> KlineDataRepairRuns => Set<KlineDataRepairRun>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<EquityPositionLot> EquityPositionLots => Set<EquityPositionLot>();
    public DbSet<EquityPositionAccount> EquityPositionAccounts => Set<EquityPositionAccount>();
    public DbSet<MbbSignalObservation> MbbSignalObservations => Set<MbbSignalObservation>();
    public DbSet<EquityCorporateAction> EquityCorporateActions => Set<EquityCorporateAction>();
    public DbSet<EquityEvent> EquityEvents => Set<EquityEvent>();
    public DbSet<EquityFundamental> EquityFundamentals => Set<EquityFundamental>();
    public DbSet<EquityForecastRecord> EquityForecastRecords => Set<EquityForecastRecord>();
    public DbSet<EquityLlmRun> EquityLlmRuns => Set<EquityLlmRun>();
    public DbSet<MarketCalendarDay> MarketCalendarDays => Set<MarketCalendarDay>();
    public DbSet<EquityCashAccount> EquityCashAccounts => Set<EquityCashAccount>();
    public DbSet<EquitySourceDocument> EquitySourceDocuments => Set<EquitySourceDocument>();
    public DbSet<EquityResearchNote> EquityResearchNotes => Set<EquityResearchNote>();
    public DbSet<EquitySourceDocumentLocation> EquitySourceDocumentLocations => Set<EquitySourceDocumentLocation>();
    public DbSet<EquitySourceAttempt> EquitySourceAttempts => Set<EquitySourceAttempt>();
    public DbSet<EquityDocumentExtraction> EquityDocumentExtractions => Set<EquityDocumentExtraction>();
    public DbSet<EquityFinancialFact> EquityFinancialFacts => Set<EquityFinancialFact>();
    public DbSet<EquityPipelineRun> EquityPipelineRuns => Set<EquityPipelineRun>();


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
            e.Property(x => x.EvidenceKind).HasMaxLength(32);
            e.Property(x => x.Provenance).HasMaxLength(256);
            e.Property(x => x.DeliveryStatus).HasMaxLength(32);
            e.Property(x => x.DeliveryError).HasMaxLength(2000);
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
            e.Property(x => x.CapabilityState).HasMaxLength(32);
            e.Property(x => x.MethodVersion).HasMaxLength(64);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsEnabled });
            e.HasIndex(x => x.DiscoveryRunId);
        });

        modelBuilder.Entity<CandleSequenceSignal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.EvidenceKind).HasMaxLength(32);
            e.Property(x => x.Provenance).HasMaxLength(256);
            e.HasIndex(x => new { x.RuleId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.RuleId, x.Symbol, x.Timeframe, x.TriggerTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CreatedAtUtc });
        });

        modelBuilder.Entity<RuleDiscoveryRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MethodVersion).HasMaxLength(64);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CreatedAtUtc });
        });

        modelBuilder.Entity<RuleDiscoveryTrial>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CandidateKey).HasMaxLength(1000);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.RejectedReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.RunId, x.TrialNumber }).IsUnique();
            e.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<InstrumentCandle>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.PriceBasis).HasMaxLength(32);
            e.Property(x => x.Venue).HasMaxLength(32);
            e.Property(x => x.SourceTimezone).HasMaxLength(64);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CloseTimeMs });
        });

        modelBuilder.Entity<KlineGapState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.StartOpenTimeMs, x.EndOpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextRetryAtUtc });
        });

        modelBuilder.Entity<KlineDataRepairRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PlanSha256).HasMaxLength(64);
            e.Property(x => x.SourceEvidenceSha256).HasMaxLength(64);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.IssueType).HasMaxLength(64);
            e.Property(x => x.SourceClassification).HasMaxLength(64);
            e.Property(x => x.SourceEvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.BeforeEvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.UnresolvedOpenTimeMsJson).HasColumnType("jsonb");
            e.HasIndex(x => x.PlanSha256).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.AppliedAtUtc });
        });

        modelBuilder.Entity<WorkerHeartbeat>(e =>
        {
            e.HasKey(x => x.WorkerName);
            e.Property(x => x.WorkerName).HasMaxLength(128);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.LastCycleDetail).HasMaxLength(1000);
        });

        modelBuilder.Entity<EquityPositionLot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Source).HasMaxLength(32);
            e.HasIndex(x => new { x.Symbol, x.AcquireDate });
        });

        modelBuilder.Entity<EquityPositionAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.HasIndex(x => x.Symbol).IsUnique();
        });

        modelBuilder.Entity<MbbSignalObservation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DecisionId).HasMaxLength(128);
            e.Property(x => x.RecorderVersion).HasMaxLength(80);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.Decision).HasMaxLength(24);
            e.Property(x => x.RulesVersion).HasMaxLength(80);
            e.Property(x => x.EvidenceStatus).HasMaxLength(64);
            e.Property(x => x.PriceBasis).HasMaxLength(32);
            e.Property(x => x.DecisionPayloadJson).HasColumnType("jsonb");
            e.Property(x => x.FillSource).HasMaxLength(64);
            e.Property(x => x.OutcomeHorizon).HasMaxLength(40);
            e.Property(x => x.CreatedAtUtc).HasDefaultValueSql("NOW()");
            e.HasIndex(x => x.DecisionId).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.SignalBarOpenTimeMs }).IsUnique();
            e.ToTable(t =>
            {
                // Decision-capable equities and their declared timeframes (EQX spec §3):
                // MBB 1h/1d, BID daily-only. A new symbol needs a new migration.
                t.HasCheckConstraint("CK_MbbSignalObservations_Scope",
                    "(\"Symbol\" = 'MBB' AND \"Timeframe\" IN ('1h','1d')) OR (\"Symbol\" = 'BID' AND \"Timeframe\" = '1d')");
                t.HasCheckConstraint("CK_MbbSignalObservations_SignalBar",
                    "\"SignalBarOpenTimeMs\" < \"SignalBarCloseTimeMs\"");
                t.HasCheckConstraint("CK_MbbSignalObservations_Chronology",
                    "\"AvailableTimeMs\" >= \"SignalBarCloseTimeMs\"");
                t.HasCheckConstraint("CK_MbbSignalObservations_Decision",
                    "\"Decision\" IN ('hold','wait','buy-more','reduce','sell','conditional-sell')");
                t.HasCheckConstraint("CK_MbbSignalObservations_ProspectiveClock",
                    "ABS(\"AvailableTimeMs\" - (EXTRACT(EPOCH FROM \"ObservedAtUtc\") * 1000)::bigint) <= 1000");
                t.HasCheckConstraint("CK_MbbSignalObservations_FillCoherence",
                    "(\"FillPrice\" IS NULL AND \"FillObservedAtUtc\" IS NULL AND \"FillSource\" IS NULL AND \"FillBarOpenTimeMs\" IS NULL) OR (\"FillPrice\" IS NOT NULL AND \"FillObservedAtUtc\" IS NOT NULL AND \"FillBarOpenTimeMs\" IS NOT NULL AND \"Decision\" IN ('buy-more','reduce','sell','conditional-sell') AND \"FillPrice\" > 0 AND \"FillObservedAtUtc\" >= \"ObservedAtUtc\" AND NULLIF(BTRIM(\"FillSource\"), '') IS NOT NULL AND \"FillBarOpenTimeMs\" >= \"AvailableTimeMs\")");
                t.HasCheckConstraint("CK_MbbSignalObservations_OutcomeCoherence",
                    "(\"OutcomeReturn\" IS NULL AND \"OutcomeObservedAtUtc\" IS NULL AND \"OutcomeHorizon\" IS NULL) OR (\"FillPrice\" IS NOT NULL AND \"FillObservedAtUtc\" IS NOT NULL AND \"OutcomeReturn\" IS NOT NULL AND \"OutcomeObservedAtUtc\" IS NOT NULL AND \"OutcomeObservedAtUtc\" >= \"FillObservedAtUtc\" AND NULLIF(BTRIM(\"OutcomeHorizon\"), '') IS NOT NULL)");
            });
        });

        modelBuilder.Entity<EquityCorporateAction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.ActionType).HasMaxLength(32);
            e.Property(x => x.RatioText).HasMaxLength(64);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Provenance).HasMaxLength(32);
            e.Property(x => x.VerificationStatus).HasMaxLength(16);
            e.HasIndex(x => new { x.Symbol, x.ExDate, x.ActionType }).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityCorporateActions_Type",
                    "\"ActionType\" IN ('cash-dividend','stock-dividend','bonus-shares','rights-issue','split','other')");
                t.HasCheckConstraint("CK_EquityCorporateActions_Verification",
                    "\"VerificationStatus\" IN ('unverified','verified','rejected')");
                t.HasCheckConstraint("CK_EquityCorporateActions_Source",
                    "\"SourceUrl\" ~ '^https?://' AND NULLIF(BTRIM(\"Provenance\"), '') IS NOT NULL");
                t.HasCheckConstraint("CK_EquityCorporateActions_Factor",
                    "\"AdjustmentFactor\" IS NULL OR \"AdjustmentFactor\" > 0");
                t.HasCheckConstraint("CK_EquityCorporateActions_ShareRatio",
                    "\"ShareRatio\" IS NULL OR \"ShareRatio\" > 0");
            });
        });

        modelBuilder.Entity<EquityEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.EventType).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Provenance).HasMaxLength(32);
            e.HasIndex(x => new { x.Symbol, x.EventType, x.EventDate, x.Title }).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityEvents_Type",
                    "\"EventType\" IN ('earnings','agm','additional-listing','other')");
                t.HasCheckConstraint("CK_EquityEvents_Source",
                    "\"SourceUrl\" ~ '^https?://' AND NULLIF(BTRIM(\"Provenance\"), '') IS NOT NULL");
            });
        });

        modelBuilder.Entity<EquityFundamental>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Period).HasMaxLength(8);
            e.Property(x => x.Metric).HasMaxLength(16);
            e.Property(x => x.Unit).HasMaxLength(16);
            e.Property(x => x.UnavailableReason).HasMaxLength(500);
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Provenance).HasMaxLength(32);
            e.HasIndex(x => new { x.Symbol, x.Period, x.Metric, x.Source }).IsUnique();
            e.Property(x => x.SourceLocation).HasMaxLength(500);
            e.HasIndex(x => x.DocumentId);
            e.HasOne<EquitySourceDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityFundamentals_Metric",
                    "\"Metric\" IN ('BVPS','PB','ROE','NIM','NPL','COVERAGE','LLR','CAR','LDR','TOTAL_ASSETS','NET_PROFIT','EQUITY')");
                t.HasCheckConstraint("CK_EquityFundamentals_Unit",
                    "\"Unit\" IN ('VND','ratio','percent')");
                t.HasCheckConstraint("CK_EquityFundamentals_Period",
                    "\"Period\" ~ '^[0-9]{4}Q[1-4]$'");
                t.HasCheckConstraint("CK_EquityFundamentals_ValueOrReason",
                    "\"Value\" IS NOT NULL OR NULLIF(BTRIM(\"UnavailableReason\"), '') IS NOT NULL");
                t.HasCheckConstraint("CK_EquityFundamentals_Source",
                    "\"SourceUrl\" ~ '^https?://' AND NULLIF(BTRIM(\"Provenance\"), '') IS NOT NULL");
            });
        });

        modelBuilder.Entity<EquityForecastRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ForecastId).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(24);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.ConfigId).HasMaxLength(40);
            e.Property(x => x.AbstainReason).HasMaxLength(500);
            e.Property(x => x.MethodVersion).HasMaxLength(80);
            e.Property(x => x.ModelId).HasMaxLength(80);
            e.Property(x => x.PromptVersion).HasMaxLength(80);
            e.Property(x => x.SchemaVersion).HasMaxLength(80);
            e.Property(x => x.InputSnapshotSha256).HasMaxLength(64);
            e.Property(x => x.OutcomeLabel).HasMaxLength(16);
            e.HasIndex(x => x.ForecastId).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.ConfigId, x.SignalBarOpenTimeMs });
            e.HasIndex(x => x.ResearchNoteId);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityForecastRecords_Source",
                    "\"Source\" IN ('quant-knn','llm-judge','session-research')");
                t.HasCheckConstraint("CK_EquityForecastRecords_Scope",
                    "\"Symbol\" IN ('MBB','BID') AND \"Timeframe\" = '1d' AND \"HorizonSessions\" > 0");
                t.HasCheckConstraint("CK_EquityForecastRecords_Chronology",
                    "\"SignalBarOpenTimeMs\" < \"SignalBarCloseTimeMs\" AND \"AvailableTimeMs\" >= \"SignalBarCloseTimeMs\"");
                t.HasCheckConstraint("CK_EquityForecastRecords_Probability",
                    "(\"ProbTargetFirst\" IS NULL OR (\"ProbTargetFirst\" >= 0 AND \"ProbTargetFirst\" <= 1)) AND (\"ProbStopFirst\" IS NULL OR (\"ProbStopFirst\" >= 0 AND \"ProbStopFirst\" <= 1)) AND (\"ProbTargetFirst\" IS NULL OR \"ProbStopFirst\" IS NULL OR \"ProbTargetFirst\" + \"ProbStopFirst\" <= 1.000001)");
                t.HasCheckConstraint("CK_EquityForecastRecords_Abstention",
                    "NOT \"Abstained\" OR (\"ProbTargetFirst\" IS NULL AND \"ProbStopFirst\" IS NULL AND NULLIF(BTRIM(\"AbstainReason\"), '') IS NOT NULL)");
                t.HasCheckConstraint("CK_EquityForecastRecords_SessionUnvalidated",
                    "\"Source\" <> 'session-research' OR (NOT \"Validated\" AND \"ResearchNoteId\" IS NOT NULL AND NULLIF(BTRIM(\"MethodVersion\"), '') IS NOT NULL)");
                t.HasCheckConstraint("CK_EquityForecastRecords_LlmUnvalidated",
                    "\"Source\" <> 'llm-judge' OR (NOT \"Validated\" AND \"LlmRunId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_EquityForecastRecords_OutcomeCoherence",
                    "(\"EntryBarOpenTimeMs\" IS NULL AND \"EntryPrice\" IS NULL AND \"OutcomeLabel\" IS NULL AND \"OutcomeReturnNet\" IS NULL AND \"ResolvedBarOpenTimeMs\" IS NULL AND \"OutcomeResolvedAtUtc\" IS NULL) OR (\"OutcomeLabel\" IN ('target-first','stop-first','timeout') AND \"EntryPrice\" > 0 AND \"OutcomeReturnNet\" IS NOT NULL AND \"EntryBarOpenTimeMs\" IS NOT NULL AND \"ResolvedBarOpenTimeMs\" IS NOT NULL AND \"EntryBarOpenTimeMs\" > \"SignalBarOpenTimeMs\" AND \"EntryBarOpenTimeMs\" >= \"AvailableTimeMs\" AND \"ResolvedBarOpenTimeMs\" >= \"EntryBarOpenTimeMs\" AND \"OutcomeResolvedAtUtc\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<EquityLlmRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.ResearchModel).HasMaxLength(80);
            e.Property(x => x.JudgeModel).HasMaxLength(80);
            e.Property(x => x.PromptVersion).HasMaxLength(80);
            e.Property(x => x.SchemaVersion).HasMaxLength(80);
            e.Property(x => x.InputSnapshotSha256).HasMaxLength(64);
            e.Property(x => x.InputSnapshotJson).HasColumnType("jsonb");
            e.Property(x => x.OutputJson).HasColumnType("jsonb");
            e.Property(x => x.UsageJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Symbol, x.CreatedAtUtc });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityLlmRuns_Status",
                    "\"Status\" IN ('succeeded','failed','unavailable')");
                t.HasCheckConstraint("CK_EquityLlmRuns_Scope",
                    "\"Symbol\" IN ('MBB','BID')");
                t.HasCheckConstraint("CK_EquityLlmRuns_Output",
                    "\"Status\" <> 'succeeded' OR \"OutputJson\" IS NOT NULL");
            });
        });

        modelBuilder.Entity<MarketCalendarDay>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Venue).HasMaxLength(16);
            e.Property(x => x.Kind).HasMaxLength(24);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.SourceTitle).HasMaxLength(500);
            e.Property(x => x.Verification).HasMaxLength(24);
            e.Property(x => x.CalendarVersion).HasMaxLength(40);
            e.HasIndex(x => new { x.Venue, x.Date }).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_MarketCalendarDays_Coverage",
                    "\"CoverageFromDate\" <= \"Date\" AND \"Date\" <= \"CoverageToDate\"");
                t.HasCheckConstraint("CK_MarketCalendarDays_Kind",
                    "\"Kind\" IN ('market-holiday','market-closure')");
                t.HasCheckConstraint("CK_MarketCalendarDays_Verification",
                    "(\"Verification\" = 'verified' AND \"SourceUrl\" ~ '^https?://') OR \"Verification\" = 'unverified-legacy'");
            });
        });

        modelBuilder.Entity<EquityCashAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AccountId).HasMaxLength(32);
            e.HasIndex(x => x.AccountId).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityCashAccounts_Cash",
                    "\"AvailableCashVnd\" IS NULL OR \"AvailableCashVnd\" >= 0");
            });
        });

        modelBuilder.Entity<EquitySourceDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Entity).HasMaxLength(200);
            e.Property(x => x.DocType).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.ContentSha256).HasMaxLength(64);
            e.Property(x => x.StoragePath).HasMaxLength(500);
            e.Property(x => x.MimeType).HasMaxLength(100);
            e.Property(x => x.ReportingPeriod).HasMaxLength(8);
            e.Property(x => x.Consolidation).HasMaxLength(16);
            e.Property(x => x.AuditStatus).HasMaxLength(16);
            e.Property(x => x.ExtractionJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Symbol, x.ContentSha256 }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.ReportingPeriod });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquitySourceDocuments_Type",
                    "\"DocType\" IN ('financial-statement','investor-presentation','disclosure','annual-report','other')");
                t.HasCheckConstraint("CK_EquitySourceDocuments_Enums",
                    "\"Consolidation\" IN ('consolidated','separate','unknown') AND \"AuditStatus\" IN ('audited','reviewed','unaudited','unknown')");
                t.HasCheckConstraint("CK_EquitySourceDocuments_Provenance",
                    "\"SourceUrl\" ~ '^https?://' AND \"ContentSha256\" ~ '^[0-9a-f]{64}$' AND NULLIF(BTRIM(\"StoragePath\"), '') IS NOT NULL AND NULLIF(BTRIM(\"Provider\"), '') IS NOT NULL AND \"ByteSize\" > 0 AND \"FetchedAtUtc\" >= \"FirstSeenAtUtc\"");
                t.HasCheckConstraint("CK_EquitySourceDocuments_Period",
                    "\"ReportingPeriod\" IS NULL OR \"ReportingPeriod\" ~ '^[0-9]{4}(Q[1-4]|H[12])?$'");
            });
        });

        modelBuilder.Entity<EquityResearchNote>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.NoteId).HasMaxLength(200);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Mode).HasMaxLength(16);
            e.Property(x => x.SnapshotId).HasMaxLength(200);
            e.Property(x => x.SnapshotSha256).HasMaxLength(64);
            e.Property(x => x.SkillVersion).HasMaxLength(80);
            e.Property(x => x.MethodVersion).HasMaxLength(80);
            e.Property(x => x.Direction).HasMaxLength(16);
            e.Property(x => x.ActionView).HasMaxLength(16);
            e.Property(x => x.ActionBasis).HasMaxLength(24);
            e.Property(x => x.BodyJson).HasColumnType("jsonb");
            e.HasIndex(x => x.NoteId).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.SignalBarOpenTimeMs });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityResearchNotes_Scope",
                    "\"Symbol\" IN ('MBB','BID') AND \"Mode\" IN ('forward','retrospective') AND \"HorizonSessions\" > 0");
                t.HasCheckConstraint("CK_EquityResearchNotes_Enums",
                    "\"Direction\" IN ('up','down','sideways','no-view') AND \"ActionView\" IN ('buy-more','hold','reduce','sell','wait','none') AND \"ActionBasis\" IN ('position-aware','market-only','insufficient-input')");
                t.HasCheckConstraint("CK_EquityResearchNotes_Provenance",
                    "\"SnapshotSha256\" ~ '^[0-9a-f]{64}$' AND NULLIF(BTRIM(\"SnapshotId\"), '') IS NOT NULL AND NULLIF(BTRIM(\"SkillVersion\"), '') IS NOT NULL AND NULLIF(BTRIM(\"MethodVersion\"), '') IS NOT NULL");
                t.HasCheckConstraint("CK_EquityResearchNotes_Chronology",
                    "\"SignalBarOpenTimeMs\" < \"SignalBarCloseTimeMs\" AND (\"Mode\" <> 'forward' OR \"AvailableTimeMs\" >= \"SignalBarCloseTimeMs\")");
                t.HasCheckConstraint("CK_EquityResearchNotes_ActionBasis",
                    "\"ActionView\" = 'none' OR \"ActionBasis\" <> 'insufficient-input'");
            });
        });

        modelBuilder.Entity<EquitySourceDocumentLocation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.ListingTitle).HasMaxLength(500);
            e.Property(x => x.HttpEtag).HasMaxLength(200);
            e.Property(x => x.HttpLastModified).HasMaxLength(100);
            e.HasIndex(x => new { x.DocumentId, x.SourceUrl }).IsUnique();
            e.HasIndex(x => x.SourceUrl);
            e.HasOne<EquitySourceDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquitySourceDocumentLocations_Url",
                    "\"SourceUrl\" ~ '^https?://' AND \"LastSeenAtUtc\" >= \"FirstSeenAtUtc\"");
            });
        });

        modelBuilder.Entity<EquitySourceAttempt>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.TargetPeriod).HasMaxLength(16);
            e.Property(x => x.DocType).HasMaxLength(32);
            e.Property(x => x.Consolidation).HasMaxLength(16);
            e.Property(x => x.Url).HasMaxLength(1000);
            e.Property(x => x.Outcome).HasMaxLength(24);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.HasIndex(x => new { x.Symbol, x.TargetPeriod, x.AttemptedAtUtc });
            e.HasIndex(x => x.RunId);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquitySourceAttempts_Outcome",
                    "\"Outcome\" IN ('listed','not-listed','downloaded','unchanged','changed','http-error','rate-limited','timeout','parse-error','budget-exhausted','too-large')");
                t.HasCheckConstraint("CK_EquitySourceAttempts_Scope",
                    "\"Symbol\" IN ('MBB','BID')");
            });
        });

        modelBuilder.Entity<EquityDocumentExtraction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExtractorVersion).HasMaxLength(80);
            e.Property(x => x.Method).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.ArtifactPath).HasMaxLength(500);
            e.Property(x => x.QualityJson).HasColumnType("jsonb");
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.DocumentId, x.ExtractorVersion }).IsUnique();
            e.HasOne<EquitySourceDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityDocumentExtractions_Enums",
                    "\"Method\" IN ('text-layer','ocr','mixed') AND \"Status\" IN ('succeeded','partial','failed')");
                t.HasCheckConstraint("CK_EquityDocumentExtractions_Pages",
                    "\"PageCount\" >= 0 AND \"PagesOcr\" >= 0 AND \"PagesOcr\" <= \"PageCount\" AND \"FinishedAtUtc\" >= \"StartedAtUtc\"");
            });
        });

        modelBuilder.Entity<EquityFinancialFact>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FactKey).HasMaxLength(300);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.MetricCode).HasMaxLength(64);
            e.Property(x => x.MetricDefinitionVersion).HasMaxLength(40);
            e.Property(x => x.MetricGroup).HasMaxLength(32);
            e.Property(x => x.Statement).HasMaxLength(24);
            e.Property(x => x.PeriodLabel).HasMaxLength(16);
            e.Property(x => x.PeriodType).HasMaxLength(16);
            e.Property(x => x.Consolidation).HasMaxLength(16);
            e.Property(x => x.Unit).HasMaxLength(16);
            e.Property(x => x.RawLabel).HasMaxLength(500);
            e.Property(x => x.RawValueText).HasMaxLength(100);
            e.Property(x => x.RawUnitText).HasMaxLength(100);
            e.Property(x => x.ColumnRole).HasMaxLength(32);
            e.Property(x => x.Location).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.ValidationJson).HasColumnType("jsonb");
            e.Property(x => x.DerivationJson).HasColumnType("jsonb");
            e.HasIndex(x => x.FactKey).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.MetricCode, x.PeriodType, x.PeriodEnd });
            e.HasIndex(x => x.DocumentId);
            e.HasOne<EquitySourceDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EquityDocumentExtraction>().WithMany().HasForeignKey(x => x.ExtractionId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityFinancialFacts_Scope",
                    "\"Symbol\" IN ('MBB','BID')");
                t.HasCheckConstraint("CK_EquityFinancialFacts_Enums",
                    "\"MetricGroup\" IN ('balance-sheet','income-statement','cash-flow','credit-quality','deposits-funding','capital','shares') AND \"Statement\" IN ('balance-sheet','income-statement','cash-flow','note','derived') AND \"PeriodType\" IN ('quarter','ytd','half-year','annual','point-in-time') AND \"Consolidation\" IN ('consolidated','separate') AND \"Unit\" IN ('VND','percent','shares','count','ratio') AND \"ColumnRole\" IN ('current','comparative','cumulative-current','cumulative-comparative','derived') AND \"Status\" IN ('extracted','validated','review-needed','quarantined')");
                t.HasCheckConstraint("CK_EquityFinancialFacts_Period",
                    "\"PeriodLabel\" ~ '^[0-9]{4}(Q[1-4]|H[12]|-9M|-6M|-3M)?$' AND (\"PeriodStart\" IS NULL OR \"PeriodStart\" <= \"PeriodEnd\") AND \"Scale\" > 0");
                t.HasCheckConstraint("CK_EquityFinancialFacts_Provenance",
                    "(\"IsDerived\" AND \"DerivationJson\" IS NOT NULL AND \"ExtractionId\" IS NULL AND \"ColumnRole\" = 'derived') OR (NOT \"IsDerived\" AND \"DerivationJson\" IS NULL AND \"ExtractionId\" IS NOT NULL AND \"PageNumber\" IS NOT NULL AND \"PageNumber\" > 0 AND NULLIF(BTRIM(\"RawValueText\"), '') IS NOT NULL AND \"ColumnRole\" <> 'derived')");
            });
        });

        modelBuilder.Entity<EquityPipelineRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Mode).HasMaxLength(16);
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.CountsJson).HasColumnType("jsonb");
            e.Property(x => x.CheckpointJson).HasColumnType("jsonb");
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.LogTail).HasMaxLength(8000);
            e.HasIndex(x => new { x.Symbol, x.StartedAtUtc });
            e.HasIndex(x => x.Symbol).IsUnique().HasFilter("\"Status\" = 'running'").HasDatabaseName("UX_EquityPipelineRuns_OneRunningPerSymbol");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_EquityPipelineRuns_Enums",
                    "\"Mode\" IN ('backfill','incremental') AND \"Trigger\" IN ('worker','admin','cli') AND \"Status\" IN ('running','succeeded','partial','failed','cancelled') AND \"Symbol\" IN ('MBB','BID')");
                t.HasCheckConstraint("CK_EquityPipelineRuns_Chronology",
                    "\"HeartbeatAtUtc\" >= \"StartedAtUtc\" AND (\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"StartedAtUtc\") AND ((\"Status\" = 'running') = (\"FinishedAtUtc\" IS NULL))");
            });
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
            e.Property(x => x.Source).HasMaxLength(128);
            e.Property(x => x.MarketType).HasMaxLength(32);
            e.Property(x => x.IsReconstructed).HasDefaultValue(true);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.AvailableTimeMs });
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
            e.Property(x => x.Source).HasMaxLength(128);
            e.Property(x => x.MarketType).HasMaxLength(32);
            e.Property(x => x.IsReconstructed).HasDefaultValue(true);
            e.HasIndex(x => new { x.Symbol, x.OpenTimeMs }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.AvailableTimeMs });
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
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.TransitionTimeMs }).IsUnique();
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

        modelBuilder.Entity<TechnicalEvidenceRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.LayerKey).HasMaxLength(64);
            e.Property(x => x.ModuleContractVersion).HasMaxLength(128);
            e.Property(x => x.ModuleContractSha256).HasMaxLength(64);
            e.Property(x => x.CalculationVersion).HasMaxLength(128);
            e.Property(x => x.Availability).HasMaxLength(32);
            e.HasIndex(x => new
                { x.Symbol, x.Timeframe, x.LayerKey, x.AsOfTimeMs, x.ModuleContractVersion,
                    x.ModuleContractSha256, x.CalculationVersion })
                .IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.AsOfTimeMs });
        });

        modelBuilder.Entity<TechnicalEvidenceRebuildCheckpoint>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.ModuleContractVersion).HasMaxLength(128);
            e.Property(x => x.ModuleContractSha256).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.ModuleContractVersion, x.ModuleContractSha256 }).IsUnique();
        });

        modelBuilder.Entity<SmartMoneyStructure>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CalculationVersion).HasMaxLength(64);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.TimeMs });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.AvailableTimeMs });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.EventType, x.OriginTimeMs, x.AvailableTimeMs, x.CalculationVersion })
                .IsUnique()
                .HasFilter("\"CalculationVersion\" = 'smc-causal-v2'");
        });
        modelBuilder.Entity<CausalSmartMoneyEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.EventId).HasMaxLength(512);
            e.Property(x => x.EventType).HasMaxLength(32);
            e.Property(x => x.State).HasMaxLength(32);
            e.Property(x => x.CalculationVersion).HasMaxLength(64);
            e.Property(x => x.DecisionEvidenceSha256).HasMaxLength(64);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.EventId, x.CalculationVersion }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.AvailableTimeMs });
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.MitigatedAtMs });
        });
        modelBuilder.Entity<CausalSmartMoneyRebuildCheckpoint>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(16);
            e.Property(x => x.CalculationVersion).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.CalculationVersion }).IsUnique();
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
