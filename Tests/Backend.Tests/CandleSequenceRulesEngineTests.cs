using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Backend.Tests;

public class CandleSequenceRulesEngineTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CandleSequenceRulesEngineTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static CandleSequenceRule CreateRule(
        string name,
        List<SequenceRuleCondition> conditions,
        bool enabled = true,
        int cooldownMinutes = 60)
    {
        return new CandleSequenceRule
        {
            Name = name,
            Description = "Unit-test rule",
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            RequiredBars = 5,
            IsEnabled = enabled,
            CooldownMinutes = cooldownMinutes,
            ConditionsJson = CandleSequenceRuleMappers.SerializeConditions(conditions),
            Action = "ALERT",
            Priority = 1
        };
    }

    private static List<KlineDto> Bars(int count, Func<int, KlineDto> factory)
    {
        return Enumerable.Range(0, count).Select(factory).ToList();
    }

    [Fact]
    public async Task EvaluateAsync_ConsecutiveGreenBars_Triggers()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<ICandleSequenceRulesEngine>();

        db.CandleSequenceRules.RemoveRange(db.CandleSequenceRules);
        db.CandleSequenceSignals.RemoveRange(db.CandleSequenceSignals);
        await db.SaveChangesAsync();

        var rule = CreateRule("3 green bars", new List<SequenceRuleCondition>
        {
            new SequenceRuleCondition
            {
                Type = "consecutive_bars",
                Direction = "green",
                Count = 3
            }
        });
        db.CandleSequenceRules.Add(rule);
        await db.SaveChangesAsync();

        var klines = Bars(5, i => new KlineDto
        {
            OpenTimeMs = 1_000_000L + i * 3_600_000L,
            Open = 64000m + i * 100m,
            High = 64200m + i * 100m,
            Low = 63900m + i * 100m,
            Close = 64100m + i * 100m,
            Volume = 100m
        });

        // Act
        var signals = await engine.EvaluateAsync("BTCUSDT", "1h", klines);

        // Assert
        Assert.Single(signals);
        Assert.Equal(rule.Name, signals[0].RuleName);
        Assert.Equal("ALERT", signals[0].Action);
    }

    [Fact]
    public async Task EvaluateAsync_DisabledRule_NotTriggered()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<ICandleSequenceRulesEngine>();

        db.CandleSequenceRules.RemoveRange(db.CandleSequenceRules);
        db.CandleSequenceSignals.RemoveRange(db.CandleSequenceSignals);
        await db.SaveChangesAsync();

        var rule = CreateRule("disabled rule", new List<SequenceRuleCondition>
        {
            new SequenceRuleCondition
            {
                Type = "consecutive_bars",
                Direction = "green",
                Count = 2
            }
        }, enabled: false);
        db.CandleSequenceRules.Add(rule);
        await db.SaveChangesAsync();

        var klines = Bars(5, i => new KlineDto
        {
            OpenTimeMs = 1_000_000L + i * 3_600_000L,
            Open = 64000m,
            High = 64200m,
            Low = 63900m,
            Close = 64100m + i * 100m,
            Volume = 100m
        });

        // Act
        var signals = await engine.EvaluateAsync("BTCUSDT", "1h", klines);

        // Assert
        Assert.Empty(signals);
    }

    [Fact]
    public async Task EvaluateAsync_RecentSignal_RespectsCooldown()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<ICandleSequenceRulesEngine>();

        db.CandleSequenceRules.RemoveRange(db.CandleSequenceRules);
        db.CandleSequenceSignals.RemoveRange(db.CandleSequenceSignals);
        await db.SaveChangesAsync();

        var rule = CreateRule("cooldown rule", new List<SequenceRuleCondition>
        {
            new SequenceRuleCondition
            {
                Type = "consecutive_bars",
                Direction = "green",
                Count = 2
            }
        }, cooldownMinutes: 60);
        db.CandleSequenceRules.Add(rule);
        await db.SaveChangesAsync();

        // Seed a recent signal so the rule is in cooldown
        db.CandleSequenceSignals.Add(new CandleSequenceSignal
        {
            RuleId = rule.Id,
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            TriggerTimeMs = 1_000_000L,
            ClosePrice = 64100m,
            Message = "recent",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30)
        });
        await db.SaveChangesAsync();

        var klines = Bars(5, i => new KlineDto
        {
            OpenTimeMs = 1_000_000L + i * 3_600_000L,
            Open = 64000m,
            High = 64200m,
            Low = 63900m,
            Close = 64100m + i * 100m,
            Volume = 100m
        });

        // Act
        var signals = await engine.EvaluateAsync("BTCUSDT", "1h", klines);

        // Assert
        Assert.Empty(signals);
    }

    [Fact]
    public async Task EvaluateAsync_OldSignal_AllowsRetrigger()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<ICandleSequenceRulesEngine>();

        db.CandleSequenceRules.RemoveRange(db.CandleSequenceRules);
        db.CandleSequenceSignals.RemoveRange(db.CandleSequenceSignals);
        await db.SaveChangesAsync();

        var rule = CreateRule("retrigger rule", new List<SequenceRuleCondition>
        {
            new SequenceRuleCondition
            {
                Type = "consecutive_bars",
                Direction = "green",
                Count = 2
            }
        }, cooldownMinutes: 60);
        db.CandleSequenceRules.Add(rule);
        await db.SaveChangesAsync();

        // Seed an old signal outside cooldown
        db.CandleSequenceSignals.Add(new CandleSequenceSignal
        {
            RuleId = rule.Id,
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            TriggerTimeMs = 1_000_000L,
            ClosePrice = 64100m,
            Message = "old",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-90)
        });
        await db.SaveChangesAsync();

        var klines = Bars(5, i => new KlineDto
        {
            OpenTimeMs = 1_000_000L + i * 3_600_000L,
            Open = 64000m,
            High = 64200m,
            Low = 63900m,
            Close = 64100m + i * 100m,
            Volume = 100m
        });

        // Act
        var signals = await engine.EvaluateAsync("BTCUSDT", "1h", klines);

        // Assert
        Assert.Single(signals);
        Assert.Equal(rule.Name, signals[0].RuleName);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyKlines_ReturnsEmpty()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICandleSequenceRulesEngine>();

        // Act
        var signals = await engine.EvaluateAsync("BTCUSDT", "1h", new List<KlineDto>());

        // Assert
        Assert.Empty(signals);
    }
}
