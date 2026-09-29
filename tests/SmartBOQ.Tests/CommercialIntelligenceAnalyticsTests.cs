using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.CognitiveBrain.Analysis;
using SmartBOQ.Infrastructure.CognitiveBrain.Currencies;
using SmartBOQ.Infrastructure.CognitiveBrain.Statistics;
using Xunit;

namespace SmartBOQ.Tests;

public class CommercialIntelligenceAnalyticsTests
{
    [Fact]
    public void CommercialAnalyzer_ComputesAccurateParetoAndTrades()
    {
        var items = new List<BoqMatchedPair>
        {
            new()
            {
                TargetItem = new BoqItem { Id = "1", BillNumber = "Bill 02-Civil", Description = "Reinforced Concrete Foundation C35", Quantity = 1000, Unit = "m3", OriginalRate = 4500 },
                InjectedRate = 5000,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                IsApproved = true
            },
            new()
            {
                TargetItem = new BoqItem { Id = "2", BillNumber = "Bill 02-Civil", Description = "Bulk Excavation in all types of soil", Quantity = 5000, Unit = "m3", OriginalRate = 80 },
                InjectedRate = 90,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                IsApproved = true
            },
            new()
            {
                TargetItem = new BoqItem { Id = "3", BillNumber = "Bill 03-MEP", Description = "High Voltage Power Cable 3x240mm2", Quantity = 500, Unit = "m", OriginalRate = 2000 },
                InjectedRate = 2200,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                IsApproved = true
            },
            new()
            {
                TargetItem = new BoqItem { Id = "4", BillNumber = "Bill 04-Landscape", Description = "Supply and planting Date Palms", Quantity = 50, Unit = "nr", OriginalRate = 12000 },
                InjectedRate = 11000,
                SimilarityScore = 0.95,
                Confidence = MatchConfidence.Exact,
                IsApproved = true
            },
            new()
            {
                TargetItem = new BoqItem { Id = "5", BillNumber = "Bill 01-Prelims", Description = "Contractor All Risk Insurance", Quantity = 1, Unit = "item", OriginalRate = 250000 },
                InjectedRate = 250000,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                IsApproved = true
            }
        };

        var analyzer = new CommercialIntelligenceAnalyzer();
        var analytics = analyzer.Analyze(items);

        // 1. Pareto ABC Check
        Assert.NotNull(analytics.ParetoAnalysis);
        Assert.True(analytics.ParetoAnalysis.TotalPricedValue > 0);
        Assert.NotEmpty(analytics.ParetoAnalysis.TopDrivers);
        Assert.Equal("Reinforced Concrete Foundation C35", analytics.ParetoAnalysis.TopDrivers[0].Description);
        Assert.Equal("A", analytics.ParetoAnalysis.TopDrivers[0].ParetoClass);

        // 2. Trade Breakdown Check
        Assert.NotEmpty(analytics.TradeBreakdowns);
        Assert.Contains(analytics.TradeBreakdowns, t => t.TradeName == "Civil & Structural");
        Assert.Contains(analytics.TradeBreakdowns, t => t.TradeName == "MEP Services");

        // 3. Variance Analysis Check
        Assert.NotNull(analytics.VarianceAnalysis);
        Assert.True(analytics.VarianceAnalysis.TotalCostEscalation > 0);
        Assert.True(analytics.VarianceAnalysis.TotalCostSavings > 0); // Date Palms decreased from 12000 to 11000

        // 4. Rate Distribution Check
        Assert.NotNull(analytics.RateDistribution);
        Assert.True(analytics.RateDistribution.Median > 0);

        // 5. Health Scorecard Check
        Assert.NotNull(analytics.HealthScorecard);
        Assert.True(analytics.HealthScorecard.OverallHealthScore >= 90.0);
        Assert.Contains("Low Risk", analytics.HealthScorecard.RiskRating);
    }

    [Fact]
    public void FxSensitivity_ComputesFluctuationImpactCorrectly()
    {
        var items = new List<BoqMatchedPair>
        {
            new()
            {
                TargetItem = new BoqItem { Id = "101", BillNumber = "Bill 05-Equipment", Description = "Imported Chiller Equipment", Quantity = 2, Unit = "item", Currency = "USD" },
                InjectedRate = 100000m, // 200,000 USD total
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                IsApproved = true
            }
        };

        var analyzer = new CommercialIntelligenceAnalyzer();
        var sensitivities = analyzer.ComputeFxSensitivity(items);

        Assert.Single(sensitivities);
        var usdSens = sensitivities[0];
        Assert.Equal(CurrencyType.USD, usdSens.ForeignCurrency);
        Assert.Equal(200000m, usdSens.ForeignAmount);
        Assert.True(usdSens.BaseCurrencyEquivalent > 0m); // In EGP
        Assert.Equal(usdSens.BaseCurrencyEquivalent * 0.05m, usdSens.ImpactAtPlus5Pct);
        Assert.Equal(usdSens.BaseCurrencyEquivalent * 0.10m, usdSens.ImpactAtPlus10Pct);
    }
}
