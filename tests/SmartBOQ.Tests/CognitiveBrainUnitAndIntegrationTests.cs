using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.CognitiveBrain.Currencies;
using SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;
using SmartBOQ.Infrastructure.CognitiveBrain.Engine;
using SmartBOQ.Infrastructure.CognitiveBrain.Statistics;
using Xunit;

namespace SmartBOQ.Tests;

public class CognitiveBrainUnitAndIntegrationTests
{
    [Fact]
    public void CognitiveTokenSet_ExtractsTokensAndComputesJaccardAccurately()
    {
        string textA = "Supply and install 16mm PVC insulated copper cables";
        string textB = "Supplying and installation of 16 mm PVC copper cable";

        var setA = CognitiveTokenSet.FromText(textA.AsSpan());
        var setB = CognitiveTokenSet.FromText(textB.AsSpan());

        Assert.True(setA.Count >= 5);
        Assert.True(setB.Count >= 5);

        double coverage = setA.CalculateTokenCoverage(setB);
        double jaccard = setA.CalculateJaccard(setB);

        Assert.True(coverage >= 0.70, $"Expected coverage >= 0.70, got {coverage}");
        Assert.True(jaccard >= 0.50, $"Expected Jaccard >= 0.50, got {jaccard}");
    }

    [Fact]
    public void QuantileScaleLattice_ClassifiesDimensionsAndDetectsMetricMultipliers()
    {
        // 1. Classification
        Assert.Equal(DimensionClass.Volume, QuantileScaleLattice.ClassifyUnit("m3"));
        Assert.Equal(DimensionClass.Volume, QuantileScaleLattice.ClassifyUnit("متر مكعب"));
        Assert.Equal(DimensionClass.Area, QuantileScaleLattice.ClassifyUnit("sqm"));
        Assert.Equal(DimensionClass.Area, QuantileScaleLattice.ClassifyUnit("m2"));
        Assert.Equal(DimensionClass.Length, QuantileScaleLattice.ClassifyUnit("lm"));
        Assert.Equal(DimensionClass.Weight, QuantileScaleLattice.ClassifyUnit("ton"));
        Assert.Equal(DimensionClass.Count, QuantileScaleLattice.ClassifyUnit("pcs"));
        Assert.Equal(DimensionClass.LumpSum, QuantileScaleLattice.ClassifyUnit("Item"));

        // 2. Unit Compatibility
        double comp = QuantileScaleLattice.EvaluateUnitCompatibility("m2", "sqm");
        Assert.True(comp >= 0.90);

        // 3. Metric 1000x multiplier detection (Ton vs Kg)
        double scaleScore = QuantileScaleLattice.EvaluateQuantityProportion(5m, 5000m, out decimal ratio);
        Assert.True(scaleScore >= 0.90);
        Assert.Equal(1000m, ratio);
    }

    [Fact]
    public void MultiCurrencyCognitiveArbitrator_DetectsCurrenciesAndIdentifiesDrift()
    {
        var arbitrator = new MultiCurrencyCognitiveArbitrator();

        // 1. Currency Detection
        Assert.Equal(CurrencyType.USD, arbitrator.DetectCurrency("$ 500.00".AsSpan()));
        Assert.Equal(CurrencyType.EUR, arbitrator.DetectCurrency("Rate in EUR".AsSpan()));
        Assert.Equal(CurrencyType.EGP, arbitrator.DetectCurrency("50,000 ج.م".AsSpan()));

        // 2. Currency Scale Drift (e.g. Rate in USD = 100 vs Rate in EGP = 4850)
        bool hasDrift = arbitrator.TryDetectCurrencyScaleDrift(4850m, 100m, out var curA, out var curB, out decimal normalizedRateB);
        Assert.True(hasDrift);
        Assert.Equal(CurrencyType.EGP, curA);
        Assert.Equal(CurrencyType.USD, curB);
        Assert.True(Math.Abs(normalizedRateB - 4850m) < 1.0m);
    }

    [Fact]
    public void StatisticalOutlierDetector_IdentifiesRateOutliersAndDecimalShifts()
    {
        var detector = new StatisticalOutlierDetector();

        var items = new List<BoqItem>
        {
            CreatePricedItem("1", "Concrete works C30", 100m, 2500m),
            CreatePricedItem("2", "Concrete works C30", 150m, 2550m),
            CreatePricedItem("3", "Concrete works C30", 120m, 2480m),
            CreatePricedItem("4", "Concrete works C30", 80m, 2520m),
            CreatePricedItem("5", "Concrete works C30", 90m, 2510m),
            // Outlier item with decimal misplacement (25000 instead of 2500 -> 10x shift)
            CreatePricedItem("6", "Concrete works C30", 110m, 25000m)
        };

        var anomalies = detector.AuditRates(items);

        Assert.NotEmpty(anomalies);
        var outlier = anomalies.FirstOrDefault(a => a.Item.ItemCode == "6");
        Assert.NotNull(outlier);
        Assert.True(outlier.IsDecimalShiftSuspected || outlier.IsRateOutlier);
    }

    [Fact]
    public void BipartiteMatchingGraph_ResolvesContestedItemsToGlobalMaximum()
    {
        // 2 targets, 2 sources
        // Target 0 prefers Source 0 (weight 0.90) and Source 1 (weight 0.85)
        // Target 1 strongly prefers Source 0 (weight 0.95) and cannot match Source 1
        var graph = new BipartiteMatchingGraph(targetCount: 2, sourceCount: 2);
        graph.AddEdge(0, 0, 0.90);
        graph.AddEdge(0, 1, 0.85);
        graph.AddEdge(1, 0, 0.95);

        var (targetToSource, matchScores) = graph.SolveOptimalAssignment();

        // Globally optimal: Target 1 gets Source 0 (0.95), Target 0 gets Source 1 (0.85). Total = 1.80
        // (Greedy would assign Target 0 -> Source 0, leaving Target 1 unmatched with total = 0.90)
        Assert.Equal(1, targetToSource[0]); // Target 0 assigned to Source 1
        Assert.Equal(0, targetToSource[1]); // Target 1 assigned to Source 0
        Assert.True(matchScores[0] > 0);
        Assert.True(matchScores[1] > 0);
    }

    [Fact]
    public async Task CognitiveAdaptiveBrain_MatchesItemsCompletelyAgnosticOfSheetNames()
    {
        var brain = new CognitiveAdaptiveBrain();

        // Target schedule in "Arbitrary_Sheet_A"
        var targets = new List<BoqItem>
        {
            new()
            {
                Id = "T1",
                BillNumber = "Arbitrary_Sheet_A",
                SheetName = "Arbitrary_Sheet_A",
                ItemCode = "E-01",
                Description = "High efficiency LED flood light fixture 400W IP66",
                Unit = "No.",
                Quantity = 50m
            },
            new()
            {
                Id = "T2",
                BillNumber = "Arbitrary_Sheet_A",
                SheetName = "Arbitrary_Sheet_A",
                ItemCode = "M-05",
                Description = "Ductile iron gate valve DN200 PN16 flanged ends",
                Unit = "Nr",
                Quantity = 12m
            }
        };

        // Source contractor database in totally different sheet "Hatchway_Vendor_Export"
        var sources = new List<BoqItem>
        {
            new()
            {
                Id = "S1",
                BillNumber = "Hatchway_Vendor_Export",
                SheetName = "Hatchway_Vendor_Export",
                ItemCode = "E-01",
                Description = "LED floodlight 400W IP66 fixture high efficiency",
                Unit = "pcs",
                Quantity = 50m,
                UnitRate = 7500m
            },
            new()
            {
                Id = "S2",
                BillNumber = "Hatchway_Vendor_Export",
                SheetName = "Hatchway_Vendor_Export",
                ItemCode = "M-05",
                Description = "Gate valve ductile iron DN 200 PN 16 flanged",
                Unit = "ea",
                Quantity = 12m,
                UnitRate = 18500m
            }
        };

        var matches = await brain.MatchItemsAsync(targets, sources, sensitivity: 0.75);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.True(m.IsApproved));
        Assert.Equal(7500m, matches[0].InjectedRate);
        Assert.Equal(18500m, matches[1].InjectedRate);
    }

    [Fact]
    public void ContractualScopeClassifier_CorrectlyIdentifiesScopes_EnglishAndArabic()
    {
        // Supply Only
        Assert.Equal(ContractualActionScope.SupplyOnly, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("Supply of Main Access Control Panel 16 reader capacity"));
        Assert.Equal(ContractualActionScope.SupplyOnly, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("توريد لوحة تحكم رئيسية بنظام الدخول الذكي"));
        Assert.Equal(ContractualActionScope.SupplyOnly, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("شراء وتوريد مهمات كابلات نحاسية"));

        // Install Only
        Assert.Equal(ContractualActionScope.InstallOnly, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("Installation of Main Access Control Panel 16 reader capacity"));
        Assert.Equal(ContractualActionScope.InstallOnly, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("تركيب لوحة تحكم رئيسية بنظام الدخول الذكي"));
        Assert.Equal(ContractualActionScope.InstallOnly, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("مصنعية تركيب وسحب كابلات"));

        // Dual Scope
        Assert.Equal(ContractualActionScope.SupplyAndInstall, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("Supply and install 24V DC Access Control Panel"));
        Assert.Equal(ContractualActionScope.SupplyAndInstall, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("توريد وتركيب لوحة تحكم رئيسية شاملة الاختبار والتشغيل"));

        // Noun phrase masking (Power supply should NOT trigger SupplyOnly for an installation item)
        Assert.Equal(ContractualActionScope.InstallOnly, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("Installation of 24V DC Power Supply unit and batteries"));

        // Demolition / Dismantle
        Assert.Equal(ContractualActionScope.Dismantle, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("Dismantling and removal of existing electrical panels"));
        Assert.Equal(ContractualActionScope.Dismantle, 
            SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope("فك وإزالة لوحات التوزيع القديمة وتخريدها"));
    }

    [Fact]
    public async Task CognitiveAdaptiveBrain_EnforcesScopeExclusivity_SupplyNeverMatchesInstall()
    {
        var brain = new CognitiveAdaptiveBrain();

        // Target: Supply only (Row 22 scenario)
        var targetSupply = new BoqItem
        {
            Id = "T_SUPPLY",
            BillNumber = "Act. Comp. D28",
            SheetName = "Act. Comp. D28",
            ItemCode = "28 13 00",
            Description = "Supply of Main Access Control Panel 16 reader capacity including enclosure",
            Unit = "No.",
            Quantity = 1m
        };

        // Source: ONLY Install candidate available (Row 26 scenario)
        var sourceInstallOnly = new List<BoqItem>
        {
            new BoqItem
            {
                Id = "S_INSTALL",
                BillNumber = "Estimate",
                SheetName = "Sheet1",
                ItemCode = "2",
                Description = "Install, testing, commissioning, of main access control panel 16 reader capacity including enclosure",
                Unit = "No.",
                Quantity = 1m,
                UnitRate = 65000m
            }
        };

        // Matching Supply target with Install-only source should be REJECTED (unmatched / fallback)
        var results = await brain.MatchItemsAsync(new[] { targetSupply }, sourceInstallOnly, sensitivity: 0.70);

        Assert.Single(results);
        Assert.Null(results[0].MatchedSourceItem); // Must NOT match install source!
        Assert.NotEqual(65000m, results[0].InjectedRate);
    }

    [Fact]
    public async Task CognitiveAdaptiveBrain_MatchesCorrespondingSupplyAndInstallCorrectly()
    {
        var brain = new CognitiveAdaptiveBrain();

        var targets = new List<BoqItem>
        {
            new BoqItem
            {
                Id = "T_SUPPLY",
                BillNumber = "Act. Comp. D28",
                SheetName = "Act. Comp. D28",
                ItemCode = "28 13 00",
                Description = "Supply of Main Access Control Panel 16 reader capacity including enclosure",
                Unit = "No.",
                Quantity = 1m
            },
            new BoqItem
            {
                Id = "T_INSTALL",
                BillNumber = "Act. Comp. D28",
                SheetName = "Act. Comp. D28",
                ItemCode = "2",
                Description = "Install, testing, commissioning, of main access control panel 16 reader capacity including enclosure",
                Unit = "No.",
                Quantity = 1m
            }
        };

        var sources = new List<BoqItem>
        {
            new BoqItem
            {
                Id = "S_INSTALL",
                BillNumber = "Estimate",
                SheetName = "Sheet1",
                ItemCode = "2",
                Description = "Install, testing, commissioning, of main access control panel 16 reader capacity including enclosure",
                Unit = "No.",
                Quantity = 1m,
                UnitRate = 65000m
            },
            new BoqItem
            {
                Id = "S_SUPPLY",
                BillNumber = "Estimate",
                SheetName = "Sheet1",
                ItemCode = "28 13 00",
                Description = "Supply of Main Access Control Panel 16 reader capacity including enclosure",
                Unit = "No.",
                Quantity = 1m,
                UnitRate = 655000m
            }
        };

        var results = await brain.MatchItemsAsync(targets, sources, sensitivity: 0.70);

        Assert.Equal(2, results.Count);
        // Supply target matched Supply source at 655,000
        Assert.Equal("S_SUPPLY", results[0].MatchedSourceItem?.Id);
        Assert.Equal(655000m, results[0].InjectedRate);

        // Install target matched Install source at 65,000
        Assert.Equal("S_INSTALL", results[1].MatchedSourceItem?.Id);
        Assert.Equal(65000m, results[1].InjectedRate);
    }

    private static BoqItem CreatePricedItem(string code, string desc, decimal qty, decimal rate)
    {
        return new BoqItem
        {
            Id = $"ITEM_{code}",
            BillNumber = "TestBill",
            SheetName = "TestSheet",
            ItemCode = code,
            Description = desc,
            Unit = "m3",
            Quantity = qty,
            UnitRate = rate,
            TotalAmount = qty * rate
        };
    }
}
