using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.CognitiveBrain.Currencies;
using SmartBOQ.Infrastructure.CognitiveBrain.Statistics;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Analysis;

/// <summary>
/// Individual high-exposure cost driver item.
/// </summary>
public sealed record TopCostDriverItem
{
    public int Rank { get; init; }
    public string Description { get; init; } = string.Empty;
    public string BillOrLocation { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public string Unit { get; init; } = string.Empty;
    public decimal UnitRate { get; init; }
    public decimal TotalAmount { get; init; }
    public double ProjectSharePercentage { get; init; }
    public string ParetoClass { get; init; } = "A";
}

/// <summary>
/// Results of Pareto (80/20) ABC analysis on tender line items.
/// </summary>
public sealed record ParetoAnalysisResult
{
    public decimal TotalPricedValue { get; init; }
    public int TotalPricedItemsCount { get; init; }

    // Category A (Critical drivers: ~80% of value, typically 15-20% of items)
    public int CategoryAItemsCount { get; init; }
    public decimal CategoryAValue { get; init; }
    public double CategoryAPercentage { get; init; }

    // Category B (Moderate drivers: ~15% of value, typically 20-30% of items)
    public int CategoryBItemsCount { get; init; }
    public decimal CategoryBValue { get; init; }
    public double CategoryBPercentage { get; init; }

    // Category C (Long-tail items: ~5% of value, typically 50% of items)
    public int CategoryCItemsCount { get; init; }
    public decimal CategoryCValue { get; init; }
    public double CategoryCPercentage { get; init; }

    public IReadOnlyList<TopCostDriverItem> TopDrivers { get; init; } = Array.Empty<TopCostDriverItem>();
}

/// <summary>
/// Breakdown of cost by engineering trade / discipline.
/// </summary>
public sealed record TradeBreakdownItem
{
    public string TradeName { get; init; } = string.Empty;
    public string TradeNameArabic { get; init; } = string.Empty;
    public int ItemsCount { get; init; }
    public decimal TotalAmount { get; init; }
    public double PercentageShare { get; init; }
    public string ParetoClass { get; init; } = "C";
    public string SamplePackages { get; init; } = string.Empty;
}

/// <summary>
/// Variance item detailing individual rate and amount change.
/// </summary>
public sealed record VarianceLineItem
{
    public required BoqItem TargetItem { get; init; }
    public decimal BaselineRate { get; init; }
    public decimal ReconciledRate { get; init; }
    public decimal RateDelta => ReconciledRate - BaselineRate;
    public double RateDeltaPercentage => BaselineRate > 0 ? (double)(RateDelta / BaselineRate) * 100.0 : 0.0;
    public decimal FinancialImpact => TargetItem.Quantity * RateDelta;
}

/// <summary>
/// Reconciliation variance and financial exposure summary.
/// </summary>
public sealed record TenderVarianceAnalysis
{
    public decimal TotalBaselineAmount { get; init; }
    public decimal TotalReconciledAmount { get; init; }
    public decimal NetFinancialVariance => TotalReconciledAmount - TotalBaselineAmount;
    public double NetVariancePercentage => TotalBaselineAmount > 0 ? (double)(NetFinancialVariance / TotalBaselineAmount) * 100.0 : 0.0;

    public decimal TotalCostEscalation { get; init; }
    public decimal TotalCostSavings { get; init; }

    public int EscalatedItemsCount { get; init; }
    public int ReducedItemsCount { get; init; }
    public int UnchangedItemsCount { get; init; }

    public IReadOnlyList<VarianceLineItem> TopEscalations { get; init; } = Array.Empty<VarianceLineItem>();
    public IReadOnlyList<VarianceLineItem> TopSavings { get; init; } = Array.Empty<VarianceLineItem>();
}

/// <summary>
/// Sensitivity analysis of foreign currency exchange rate fluctuations on project value.
/// </summary>
public sealed record FxCurrencySensitivity
{
    public CurrencyType ForeignCurrency { get; init; }
    public decimal ForeignAmount { get; init; }
    public decimal CurrentExchangeRate { get; init; }
    public decimal BaseCurrencyEquivalent { get; init; }

    public decimal ImpactAtPlus5Pct => BaseCurrencyEquivalent * 0.05m;
    public decimal ImpactAtPlus10Pct => BaseCurrencyEquivalent * 0.10m;
    public decimal ImpactAtMinus5Pct => -BaseCurrencyEquivalent * 0.05m;
    public decimal ImpactAtMinus10Pct => -BaseCurrencyEquivalent * 0.10m;
}

/// <summary>
/// High-level commercial health and risk scorecard for tender governance.
/// </summary>
public sealed record TenderHealthScorecard
{
    public double OverallHealthScore { get; init; } // 0 - 100
    public string RiskRating { get; init; } = "Low"; // Low, Moderate, High, Critical
    public int TotalLineItems { get; init; }
    public int PricedItemsCount { get; init; }
    public double PricingCoveragePercentage { get; init; }
    public double ExactMatchRatio { get; init; }
    public int ProvisionalSumsCount { get; init; }
    public decimal ProvisionalSumsTotalAmount { get; init; }
    public double ProvisionalSumsPercentageOfTender { get; init; }
    public int VariationOrdersCount { get; init; }
    public int StatisticalAnomaliesCount { get; init; }
    public string GovernanceSummary { get; init; } = string.Empty;
}

/// <summary>
/// Master aggregated container for all commercial, financial, and statistical analytics.
/// </summary>
public sealed record ComprehensiveTenderAnalytics
{
    public required TenderHealthScorecard HealthScorecard { get; init; }
    public required ParetoAnalysisResult ParetoAnalysis { get; init; }
    public required IReadOnlyList<TradeBreakdownItem> TradeBreakdowns { get; init; }
    public required TenderVarianceAnalysis VarianceAnalysis { get; init; }
    public required PriceDistributionSummary RateDistribution { get; init; }
    public required IReadOnlyList<RateAnomalyReport> StatisticalAnomalies { get; init; }
    public required IReadOnlyList<FxCurrencySensitivity> CurrencySensitivities { get; init; }
}
