using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.CognitiveBrain.Currencies;
using SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;
using SmartBOQ.Infrastructure.CognitiveBrain.Statistics;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Analysis;

/// <summary>
/// Master Commercial and Statistical Intelligence Analyzer for Bill of Quantities.
/// Computes Pareto 80/20 ABC cost distribution, trade classification, rate variance,
/// FX foreign currency sensitivities, and tender health & risk scorecards.
/// </summary>
public sealed class CommercialIntelligenceAnalyzer
{
    private readonly StatisticalOutlierDetector _outlierDetector;
    private readonly ExchangeRateTable _exchangeTable;

    public CommercialIntelligenceAnalyzer(
        StatisticalOutlierDetector? outlierDetector = null,
        ExchangeRateTable? exchangeTable = null)
    {
        _outlierDetector = outlierDetector ?? new StatisticalOutlierDetector();
        _exchangeTable = exchangeTable ?? new ExchangeRateTable();
    }

    /// <summary>
    /// Executes end-to-end commercial and statistical analytics on reconciled BOQ matched pairs.
    /// </summary>
    public ComprehensiveTenderAnalytics Analyze(IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var pareto = ComputeParetoAnalysis(matchedPairs);
        var trades = ComputeTradeBreakdown(matchedPairs);
        var variance = ComputeVarianceAnalysis(matchedPairs);
        var distribution = ComputeRateDistribution(matchedPairs);
        var anomalies = ComputeStatisticalAnomalies(matchedPairs);
        var fxSensitivities = ComputeFxSensitivity(matchedPairs);
        var scorecard = ComputeHealthScorecard(matchedPairs, pareto, variance, anomalies);

        return new ComprehensiveTenderAnalytics
        {
            HealthScorecard = scorecard,
            ParetoAnalysis = pareto,
            TradeBreakdowns = trades,
            VarianceAnalysis = variance,
            RateDistribution = distribution,
            StatisticalAnomalies = anomalies,
            CurrencySensitivities = fxSensitivities
        };
    }

    /// <summary>
    /// Computes Pareto (80/20) ABC classification of all priced items.
    /// </summary>
    public ParetoAnalysisResult ComputeParetoAnalysis(IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var pricedItems = matchedPairs
            .Where(p => p.InjectedRate.HasValue && p.InjectedRate > 0 && p.TargetItem.Quantity > 0 &&
                        p.TargetItem.Type != BoqItemType.ProvisionalSum && !p.TargetItem.IsProtected)
            .Select(p => new
            {
                Item = p.TargetItem,
                Rate = p.InjectedRate!.Value,
                Amount = p.TargetItem.Quantity * p.InjectedRate!.Value,
                p.SimilarityScore,
                p.Confidence
            })
            .OrderByDescending(x => x.Amount)
            .ToList();

        decimal totalValue = pricedItems.Sum(x => x.Amount);
        if (totalValue <= 0m || pricedItems.Count == 0)
        {
            return new ParetoAnalysisResult();
        }

        int countA = 0, countB = 0, countC = 0;
        decimal valA = 0m, valB = 0m, valC = 0m;
        decimal runningSum = 0m;

        var topDrivers = new List<TopCostDriverItem>();

        for (int i = 0; i < pricedItems.Count; i++)
        {
            var p = pricedItems[i];
            runningSum += p.Amount;
            double cumulativeShare = (double)(runningSum / totalValue);

            string pClass;
            if (cumulativeShare <= 0.80 || countA == 0)
            {
                pClass = "A";
                countA++;
                valA += p.Amount;
            }
            else if (cumulativeShare <= 0.95 || countB == 0)
            {
                pClass = "B";
                countB++;
                valB += p.Amount;
            }
            else
            {
                pClass = "C";
                countC++;
                valC += p.Amount;
            }

            if (i < 15)
            {
                string desc = !string.IsNullOrWhiteSpace(p.Item.Description) ? p.Item.Description : p.Item.LineItemText;
                topDrivers.Add(new TopCostDriverItem
                {
                    Rank = i + 1,
                    Description = desc.Length > 80 ? desc[..80] + "..." : desc,
                    BillOrLocation = !string.IsNullOrEmpty(p.Item.BillNumber) ? p.Item.BillNumber : p.Item.SheetName,
                    Quantity = p.Item.Quantity,
                    Unit = p.Item.Unit,
                    UnitRate = p.Rate,
                    TotalAmount = p.Amount,
                    ProjectSharePercentage = (double)(p.Amount / totalValue) * 100.0,
                    ParetoClass = pClass
                });
            }
        }

        return new ParetoAnalysisResult
        {
            TotalPricedValue = totalValue,
            TotalPricedItemsCount = pricedItems.Count,
            CategoryAItemsCount = countA,
            CategoryAValue = valA,
            CategoryAPercentage = (double)(valA / totalValue) * 100.0,
            CategoryBItemsCount = countB,
            CategoryBValue = valB,
            CategoryBPercentage = (double)(valB / totalValue) * 100.0,
            CategoryCItemsCount = countC,
            CategoryCValue = valC,
            CategoryCPercentage = (double)(valC / totalValue) * 100.0,
            TopDrivers = topDrivers
        };
    }

    /// <summary>
    /// Breaks down costs into major engineering trades and disciplines.
    /// </summary>
    public IReadOnlyList<TradeBreakdownItem> ComputeTradeBreakdown(IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var pricedItems = matchedPairs
            .Where(p => p.InjectedRate.HasValue && p.InjectedRate > 0)
            .ToList();

        decimal grandTotal = pricedItems.Sum(p => p.TargetItem.Quantity * p.InjectedRate!.Value);
        if (grandTotal <= 0m) return Array.Empty<TradeBreakdownItem>();

        var tradeBuckets = new Dictionary<string, (string Arabic, List<BoqMatchedPair> Items)>
        {
            ["Preliminaries"] = ("شروط عامة وتجهيزات موقع", new List<BoqMatchedPair>()),
            ["Civil & Structural"] = ("أعمال مدنية وإنشائية", new List<BoqMatchedPair>()),
            ["Architectural & Finishes"] = ("تشطيبات ومعماري", new List<BoqMatchedPair>()),
            ["MEP Services"] = ("كهروميكانيك ومحطات", new List<BoqMatchedPair>()),
            ["Infrastructure & Wet Networks"] = ("بنية تحتية وشبكات", new List<BoqMatchedPair>()),
            ["Landscape & External Works"] = ("أعمال الموقع العام واللاندسكيب", new List<BoqMatchedPair>()),
            ["General & Miscellaneous"] = ("أعمال عامة ومتنوعة", new List<BoqMatchedPair>())
        };

        foreach (var p in pricedItems)
        {
            string text = $"{p.TargetItem.SheetName} {p.TargetItem.BillNumber} {p.TargetItem.Description}".ToLowerInvariant();

            if (text.Contains("prelim") || text.Contains("general requirement") || text.Contains("insurance") || text.Contains("شروط عامة"))
            {
                tradeBuckets["Preliminaries"].Items.Add(p);
            }
            else if (text.Contains("concrete") || text.Contains("reinforcement") || text.Contains("rebar") || text.Contains("excavat") || text.Contains("خرسان") || text.Contains("حفر") || text.Contains("ردم") || text.Contains("حديد"))
            {
                tradeBuckets["Civil & Structural"].Items.Add(p);
            }
            else if (text.Contains("mep") || text.Contains("cable") || text.Contains("panel") || text.Contains("generator") || text.Contains("chiller") || text.Contains("pump") || text.Contains("light") || text.Contains("كابل") || text.Contains("انارة") || text.Contains("تكييف"))
            {
                tradeBuckets["MEP Services"].Items.Add(p);
            }
            else if (text.Contains("infra") || text.Contains("pipe") || text.Contains("drain") || text.Contains("sewer") || text.Contains("water") || text.Contains("network") || text.Contains("شبك") || text.Contains("مواسير"))
            {
                tradeBuckets["Infrastructure & Wet Networks"].Items.Add(p);
            }
            else if (text.Contains("landscap") || text.Contains("plant") || text.Contains("irrigation") || text.Contains("golf") || text.Contains("زراع") || text.Contains("ري") || text.Contains("مسطحات"))
            {
                tradeBuckets["Landscape & External Works"].Items.Add(p);
            }
            else if (text.Contains("finish") || text.Contains("paint") || text.Contains("plaster") || text.Contains("tile") || text.Contains("marble") || text.Contains("masonry") || text.Contains("بياض") || text.Contains("دهان") || text.Contains("سيراميك") || text.Contains("مباني"))
            {
                tradeBuckets["Architectural & Finishes"].Items.Add(p);
            }
            else
            {
                tradeBuckets["General & Miscellaneous"].Items.Add(p);
            }
        }

        var results = new List<TradeBreakdownItem>();
        decimal runningCumulative = 0m;

        foreach (var kvp in tradeBuckets.OrderByDescending(x => x.Value.Items.Sum(i => i.TargetItem.Quantity * i.InjectedRate!.Value)))
        {
            var items = kvp.Value.Items;
            if (items.Count == 0) continue;

            decimal tradeTotal = items.Sum(i => i.TargetItem.Quantity * i.InjectedRate!.Value);
            double share = (double)(tradeTotal / grandTotal) * 100.0;
            runningCumulative += tradeTotal;
            double cumPct = (double)(runningCumulative / grandTotal);

            string pClass = cumPct <= 0.80 ? "A" : (cumPct <= 0.95 ? "B" : "C");

            var samplePackages = string.Join(", ", items.Select(i => i.TargetItem.SheetName).Distinct().Take(3));

            results.Add(new TradeBreakdownItem
            {
                TradeName = kvp.Key,
                TradeNameArabic = kvp.Value.Arabic,
                ItemsCount = items.Count,
                TotalAmount = tradeTotal,
                PercentageShare = share,
                ParetoClass = pClass,
                SamplePackages = samplePackages
            });
        }

        return results;
    }

    /// <summary>
    /// Analyzes variance between baseline tender rates and reconciled rates.
    /// </summary>
    public TenderVarianceAnalysis ComputeVarianceAnalysis(IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var variancePairs = matchedPairs
            .Where(p => p.InjectedRate.HasValue && p.TargetItem.OriginalRate.HasValue && p.TargetItem.Quantity > 0)
            .Select(p => new VarianceLineItem
            {
                TargetItem = p.TargetItem,
                BaselineRate = p.TargetItem.OriginalRate!.Value,
                ReconciledRate = p.InjectedRate!.Value
            })
            .ToList();

        if (variancePairs.Count == 0)
        {
            return new TenderVarianceAnalysis();
        }

        decimal totalBaseline = variancePairs.Sum(v => v.TargetItem.Quantity * v.BaselineRate);
        decimal totalReconciled = variancePairs.Sum(v => v.TargetItem.Quantity * v.ReconciledRate);

        decimal escalations = variancePairs.Where(v => v.RateDelta > 0).Sum(v => v.FinancialImpact);
        decimal savings = variancePairs.Where(v => v.RateDelta < 0).Sum(v => Math.Abs(v.FinancialImpact));

        int escalatedCount = variancePairs.Count(v => v.RateDelta > 0.01m);
        int reducedCount = variancePairs.Count(v => v.RateDelta < -0.01m);
        int unchangedCount = variancePairs.Count(v => Math.Abs(v.RateDelta) <= 0.01m);

        var topEscalations = variancePairs.Where(v => v.RateDelta > 0)
                                          .OrderByDescending(v => v.FinancialImpact)
                                          .Take(10)
                                          .ToList();

        var topSavings = variancePairs.Where(v => v.RateDelta < 0)
                                      .OrderByDescending(v => Math.Abs(v.FinancialImpact))
                                      .Take(10)
                                      .ToList();

        return new TenderVarianceAnalysis
        {
            TotalBaselineAmount = totalBaseline,
            TotalReconciledAmount = totalReconciled,
            TotalCostEscalation = escalations,
            TotalCostSavings = savings,
            EscalatedItemsCount = escalatedCount,
            ReducedItemsCount = reducedCount,
            UnchangedItemsCount = unchangedCount,
            TopEscalations = topEscalations,
            TopSavings = topSavings
        };
    }

    /// <summary>
    /// Computes statistical rate distribution percentiles.
    /// </summary>
    public PriceDistributionSummary ComputeRateDistribution(IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var rates = matchedPairs
            .Where(p => p.InjectedRate.HasValue && p.InjectedRate > 0)
            .Select(p => p.InjectedRate!.Value);

        return RateDistributionModel.ComputeDistribution(rates);
    }

    /// <summary>
    /// Performs stratified rate anomaly and outlier auditing.
    /// </summary>
    public IReadOnlyList<RateAnomalyReport> ComputeStatisticalAnomalies(IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var itemsToAudit = matchedPairs
            .Where(p => p.InjectedRate.HasValue && p.InjectedRate > 0)
            .Select(p => p.TargetItem with { UnitRate = p.InjectedRate })
            .ToList();

        return _outlierDetector.AuditRatesStratified(itemsToAudit);
    }

    /// <summary>
    /// Computes foreign currency sensitivity against base currency (EGP).
    /// </summary>
    public IReadOnlyList<FxCurrencySensitivity> ComputeFxSensitivity(IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var currencies = matchedPairs
            .Where(p => !string.IsNullOrEmpty(p.TargetItem.Currency) && p.InjectedRate.HasValue)
            .GroupBy(p => p.TargetItem.Currency.Trim().ToUpperInvariant())
            .ToList();

        var list = new List<FxCurrencySensitivity>();

        foreach (var g in currencies)
        {
            string curName = g.Key;
            decimal totalInCur = g.Sum(x => x.TargetItem.Quantity * x.InjectedRate!.Value);

            CurrencyType curType = curName switch
            {
                "USD" or "DOLLAR" => CurrencyType.USD,
                "EUR" or "EURO" => CurrencyType.EUR,
                "SAR" => CurrencyType.SAR,
                "AED" => CurrencyType.AED,
                "GBP" => CurrencyType.GBP,
                "KWD" => CurrencyType.KWD,
                _ => CurrencyType.Unknown
            };

            if (curType != CurrencyType.Unknown && curType != CurrencyType.EGP)
            {
                decimal parity = _exchangeTable.GetParityRatio(curType, CurrencyType.EGP);
                list.Add(new FxCurrencySensitivity
                {
                    ForeignCurrency = curType,
                    ForeignAmount = totalInCur,
                    CurrentExchangeRate = parity,
                    BaseCurrencyEquivalent = totalInCur * parity
                });
            }
        }

        return list;
    }

    private static TenderHealthScorecard ComputeHealthScorecard(
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        ParetoAnalysisResult pareto,
        TenderVarianceAnalysis variance,
        IReadOnlyList<RateAnomalyReport> anomalies)
    {
        int total = matchedPairs.Count;
        if (total == 0) return new TenderHealthScorecard();

        int priced = matchedPairs.Count(p => p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum);
        int exact = matchedPairs.Count(p => p.Confidence == MatchConfidence.Exact);
        int psCount = matchedPairs.Count(p => p.TargetItem.Type == BoqItemType.ProvisionalSum || p.TargetItem.IsProtected);
        decimal psAmount = matchedPairs.Where(p => p.TargetItem.Type == BoqItemType.ProvisionalSum && p.TargetItem.TotalAmount.HasValue)
                                       .Sum(p => p.TargetItem.TotalAmount!.Value);
        int voCount = matchedPairs.Count(p => p.IsVariationOrder);

        double coverage = (double)priced / Math.Max(1, total - psCount) * 100.0;
        double exactPct = total > 0 ? (double)exact / total * 100.0 : 0.0;

        // Health Score calculation (0 - 100)
        double score = (coverage * 0.50) + (exactPct * 0.30);
        if (anomalies.Count == 0) score += 10.0;
        else score += Math.Max(0.0, 10.0 - (anomalies.Count * 0.5));
        if (voCount == 0) score += 10.0;
        else score += Math.Max(0.0, 10.0 - (voCount * 0.2));

        score = Math.Min(100.0, Math.Max(0.0, score));

        string risk = score switch
        {
            >= 90.0 => "Low Risk (Grade A Tender)",
            >= 75.0 => "Moderate Risk (Commercial Review Recommended)",
            >= 60.0 => "High Risk (Significant Scope/Price Gaps)",
            _ => "Critical Risk (Unsound Tender Architecture)"
        };

        string summary = $"Priced coverage: {coverage:F1}%, Exact match: {exactPct:F1}%, {psCount} Provisional Sums shielded, {voCount} Variation Orders, {anomalies.Count} statistical rate alerts.";

        return new TenderHealthScorecard
        {
            OverallHealthScore = Math.Round(score, 1),
            RiskRating = risk,
            TotalLineItems = total,
            PricedItemsCount = priced,
            PricingCoveragePercentage = coverage,
            ExactMatchRatio = exactPct,
            ProvisionalSumsCount = psCount,
            ProvisionalSumsTotalAmount = psAmount,
            ProvisionalSumsPercentageOfTender = pareto.TotalPricedValue > 0 ? (double)(psAmount / pareto.TotalPricedValue) * 100.0 : 0.0,
            VariationOrdersCount = voCount,
            StatisticalAnomaliesCount = anomalies.Count,
            GovernanceSummary = summary
        };
    }
}
