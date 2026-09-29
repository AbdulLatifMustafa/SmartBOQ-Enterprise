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

    [Fact]
    public async Task Integration_ElectricalAndConsultantBoq_MatchesWithHighFidelityAndPreservesScope()
    {
        string elecPath = @"C:\Users\BodyBoy\Desktop\logs\BOQs\Electrical.xlsx";
        string targetPath = @"C:\Users\BodyBoy\Desktop\logs\BOQs\BOQs\09_E_2   Infra- Phase 1_Rev_02.xlsx";
        if (!System.IO.File.Exists(elecPath) || !System.IO.File.Exists(targetPath)) return;

        var reader = new SmartBOQ.Infrastructure.Parsers.UniversalAdaptiveBoqReader();
        var consultantReader = new SmartBOQ.Infrastructure.Parsers.HierarchicalBoqReader();
        var elecItems = await reader.ReadContractorFlatBoqAsync(elecPath);
        var targetSheets = await consultantReader.ReadConsultantHierarchicalBoqAsync(targetPath);
        var targetItems = targetSheets.SelectMany(s => s.Items).ToList();

        var d28Targets = targetItems.Where(t => t.SheetName.Contains("Act. Comp. D28") || t.BillNumber.Contains("Act. Comp. D28")).ToList();
        Assert.True(elecItems.Count > 0);
        Assert.True(d28Targets.Count > 0);

        var brain = new CognitiveAdaptiveBrain();
        var pairs = await brain.MatchItemsAsync(d28Targets, elecItems, sensitivity: 0.70);

        var match22 = pairs.FirstOrDefault(p => p.TargetItem.AnchorRowIndex == 22);
        var match26 = pairs.FirstOrDefault(p => p.TargetItem.AnchorRowIndex == 26);

        // Row 22 is Supply Main Access Control Panel (~655k)
        Assert.NotNull(match22);
        Assert.NotNull(match22.MatchedSourceItem);
        Assert.Equal(655173.78m, match22.InjectedRate);
        Assert.True(match22.SimilarityScore >= 0.90);

        // Row 26 is Install Main Access Control Panel (~65k)
        Assert.NotNull(match26);
        Assert.NotNull(match26.MatchedSourceItem);
        Assert.Equal(65517.38m, match26.InjectedRate);
        Assert.True(match26.SimilarityScore >= 0.90);

        // Ensure 100% of D28 items matched successfully
        Assert.Equal(d28Targets.Count, pairs.Count(p => p.MatchedSourceItem != null));
    }

    [Fact]
    public async Task Integration_ExportCaravanBoq_ProducesCleanNumbersWithoutFormulasOrPoundSigns()
    {
        string templatePath = @"C:\Users\BodyBoy\Desktop\logs\BOQs\BOQs\07_E_1_2  Design Build Fixed CCTV Caravan_Rev_02.xlsx";
        string elecPath = @"C:\Users\BodyBoy\Desktop\logs\BOQs\Electrical.xlsx";
        if (!System.IO.File.Exists(templatePath) || !System.IO.File.Exists(elecPath)) return;

        var consultantReader = new SmartBOQ.Infrastructure.Parsers.HierarchicalBoqReader();
        var contractorReader = new SmartBOQ.Infrastructure.Parsers.UniversalAdaptiveBoqReader();
        var targetSheets = await consultantReader.ReadConsultantHierarchicalBoqAsync(templatePath);
        var targetItems = targetSheets.SelectMany(s => s.Items).ToList();
        var elecItems = await contractorReader.ReadContractorFlatBoqAsync(elecPath);

        var brain = new CognitiveAdaptiveBrain();
        var pairs = await brain.MatchItemsAsync(targetItems, elecItems, sensitivity: 0.70);

        string testOutPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Caravan_Export_Test_Clean.xlsx");
        var exporter = new SmartBOQ.Infrastructure.Export.ClosedXmlExporter();
        await exporter.ExportPricedBoqAsync(templatePath, testOutPath, pairs, elecPath, enableDynamicLinking: false);

        using var wb = new ClosedXML.Excel.XLWorkbook(testOutPath);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Contains("Elec") && w.Name.Contains("26")) ?? wb.Worksheet(1);
        var colF = ws.Column(6);
        var colG = ws.Column(7);
        var cell14F = ws.Cell("F14");
        var cell14G = ws.Cell("G14");
        var cell24F = ws.Cell("F24");

        // 1. Column Width MUST be at least 16 to fit 6-figure and 7-figure numbers without '####'
        Assert.True(colF.Width >= 16.0, $"Column F width ({colF.Width}) should be >= 16 to prevent '####'");
        Assert.True(colG.Width >= 16.0, $"Column G width ({colG.Width}) should be >= 16 to prevent '####'");

        // 2. F14 MUST contain clean numeric rate (264,679.56) without obsolete formula
        Assert.False(cell14F.HasFormula, "Cell F14 must NOT have any formula");
        Assert.Equal(264679.56, cell14F.GetDouble(), 2);

        // 3. F24 MUST contain clean numeric rate (132,507.26) without obsolete formula
        Assert.False(cell24F.HasFormula, "Cell F24 must NOT have any formula");
        Assert.Equal(132507.26, cell24F.GetDouble(), 2);

        // 4. Verify rates are completely numeric with no formula strings
        Assert.True(cell14F.DataType == ClosedXML.Excel.XLDataType.Number, "Cell F14 must be a Number");
        Assert.True(cell24F.DataType == ClosedXML.Excel.XLDataType.Number, "Cell F24 must be a Number");
    }

    [Fact]
    public async Task ExportPricedBoqAsync_WithDynamicLinking_RelativizesExternalLinksAndInjectsFormulas()
    {
        string templatePath = @"C:\Users\BodyBoy\Desktop\logs\BOQs\BOQs\07_E_1_2  Design Build Fixed CCTV Caravan_Rev_02.xlsx";
        string elecPath = @"C:\Users\BodyBoy\Desktop\logs\BOQs\Electrical.xlsx";
        if (!System.IO.File.Exists(templatePath) || !System.IO.File.Exists(elecPath)) return;

        var consultantReader = new SmartBOQ.Infrastructure.Parsers.HierarchicalBoqReader();
        var contractorReader = new SmartBOQ.Infrastructure.Parsers.UniversalAdaptiveBoqReader();
        var targetSheets = await consultantReader.ReadConsultantHierarchicalBoqAsync(templatePath);
        var targetItems = targetSheets.SelectMany(s => s.Items).ToList();
        var elecItems = await contractorReader.ReadContractorFlatBoqAsync(elecPath);

        var brain = new CognitiveAdaptiveBrain();
        var pairs = await brain.MatchItemsAsync(targetItems, elecItems, sensitivity: 0.70);

        string testOutDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartBOQ_Dynamic_Test_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(testOutDir);
        string testOutPath = System.IO.Path.Combine(testOutDir, "07_E_1_2  Design Build Fixed CCTV Caravan_Rev_02_Reconciled.xlsx");

        // Copy contractor file to same directory to mirror production export behavior
        string localElecPath = System.IO.Path.Combine(testOutDir, "Electrical.xlsx");
        System.IO.File.Copy(elecPath, localElecPath, overwrite: true);

        var exporter = new SmartBOQ.Infrastructure.Export.ClosedXmlExporter();
        await exporter.ExportPricedBoqAsync(templatePath, testOutPath, pairs, localElecPath, enableDynamicLinking: true);

        // 1. Verify zip structure and relativized external links
        using (var zip = System.IO.Compression.ZipFile.OpenRead(testOutPath))
        {
            var relsEntry = zip.GetEntry("xl/externalLinks/_rels/externalLink3.xml.rels");
            Assert.NotNull(relsEntry);
            using var reader = new System.IO.StreamReader(relsEntry.Open());
            string relsXml = reader.ReadToEnd();
            Assert.Contains("Target=\"Electrical.xlsx\"", relsXml);
            Assert.DoesNotContain("/CIVIL/SHARE", relsXml);

            // 2. Verify sheet7.xml has external formula and cached rate for F14
            var sheet7 = zip.GetEntry("xl/worksheets/sheet7.xml");
            Assert.NotNull(sheet7);
            using var s7Reader = new System.IO.StreamReader(sheet7.Open());
            string s7Xml = s7Reader.ReadToEnd();
            Assert.Contains("r=\"F14\"", s7Xml);
            Assert.Contains("[3]Estimate!$I$596", s7Xml);
            Assert.Contains("264679.56", s7Xml);

            // 3. Verify workbook.xml calcPr has fullCalcOnLoad
            var wbEntry = zip.GetEntry("xl/workbook.xml");
            Assert.NotNull(wbEntry);
            using var wbReader = new System.IO.StreamReader(wbEntry.Open());
            string wbXml = wbReader.ReadToEnd();
            Assert.Contains("fullCalcOnLoad=\"1\"", wbXml);

            // 4. Verify sheetProtection was stripped so engineer can edit freely without password
            Assert.DoesNotContain("<sheetProtection", s7Xml);
            Assert.DoesNotContain("<x:sheetProtection", s7Xml);
        }

        // 5. Test Grand Main Summary external link relativization to _Reconciled packages
        string summaryTemplate = @"C:\Users\BodyBoy\Desktop\logs\BOQs\BOQs\00_A_0  Grand Main Summary _55018602_Rev_02.xlsx";
        if (System.IO.File.Exists(summaryTemplate))
        {
            string summaryOut = System.IO.Path.Combine(testOutDir, "00_A_0  Grand Main Summary _55018602_Rev_02_Reconciled.xlsx");
            await exporter.ExportPricedBoqAsync(summaryTemplate, summaryOut, new List<BoqMatchedPair>(), localElecPath, enableDynamicLinking: true);

            using var zipSummary = System.IO.Compression.ZipFile.OpenRead(summaryOut);
            var link8 = zipSummary.GetEntry("xl/externalLinks/_rels/externalLink8.xml.rels");
            Assert.NotNull(link8);
            using var l8Reader = new System.IO.StreamReader(link8.Open());
            string l8Xml = l8Reader.ReadToEnd();
            Assert.Contains("08_E_1_3", l8Xml);
            Assert.Contains("_Reconciled.xlsx", l8Xml);

            // Verify sheet protection is also stripped on Grand Main Summary
            var s1 = zipSummary.GetEntry("xl/worksheets/sheet1.xml");
            Assert.NotNull(s1);
            using var s1Reader = new System.IO.StreamReader(s1.Open());
            string s1Xml = s1Reader.ReadToEnd();
            Assert.DoesNotContain("<sheetProtection", s1Xml);
            Assert.DoesNotContain("<x:sheetProtection", s1Xml);
        }

        // Clean up
        try { System.IO.Directory.Delete(testOutDir, true); } catch { }
    }

    [Fact]
    public void DynamicClassification_RelativizesExternalLinksWithoutHardcodedNames()
    {
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartBOQ_DynamicClassify_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(tempDir);
        string testZipPath = System.IO.Path.Combine(tempDir, "TestConsultantSummary_Reconciled.xlsx");

        try
        {
            // Create a minimal OpenXML workbook package with external links to arbitrary non-hardcoded files
            using (var zip = System.IO.Compression.ZipFile.Open(testZipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var wbEntry = zip.CreateEntry("xl/workbook.xml");
                using (var w = new System.IO.StreamWriter(wbEntry.Open()))
                {
                    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\r\n" +
                            "  <sheets><sheet name=\"Summary\" sheetId=\"1\" r:id=\"rId1\"/></sheets>\r\n" +
                            "  <externalReferences>\r\n" +
                            "    <externalReference r:id=\"rId2\"/>\r\n" +
                            "    <externalReference r:id=\"rId3\"/>\r\n" +
                            "  </externalReferences>\r\n" +
                            "</workbook>");
                }

                var wbRelsEntry = zip.CreateEntry("xl/_rels/workbook.xml.rels");
                using (var w = new System.IO.StreamWriter(wbRelsEntry.Open()))
                {
                    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                            "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>\r\n" +
                            "  <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink\" Target=\"externalLinks/externalLink1.xml\"/>\r\n" +
                            "  <Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink\" Target=\"externalLinks/externalLink2.xml\"/>\r\n" +
                            "</Relationships>");
                }

                var link1Rels = zip.CreateEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
                using (var w = new System.IO.StreamWriter(link1Rels.Open()))
                {
                    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                            "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath\" Target=\"file:///D:/Projects/Quotes/CustomVendorRates_2026.xlsx\" TargetMode=\"External\"/>\r\n" +
                            "</Relationships>");
                }

                var link2Rels = zip.CreateEntry("xl/externalLinks/_rels/externalLink2.xml.rels");
                using (var w = new System.IO.StreamWriter(link2Rels.Open()))
                {
                    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                            "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath\" Target=\"file:///D:/Tender/Docs/Project_Civil_Package_Rev01.xlsx\" TargetMode=\"External\"/>\r\n" +
                            "</Relationships>");
                }
            }

            var contractorFiles = new List<string> { @"D:\Projects\Quotes\CustomVendorRates_2026.xlsx" };
            var targetFiles = new List<string> { @"D:\Tender\Docs\Project_Civil_Package_Rev01.xlsx", @"D:\Tender\Docs\TestConsultantSummary.xlsx" };

            // Execute dynamic linking with dynamic adaptive classification
            SmartBOQ.Infrastructure.Export.ClosedXmlExporter.InjectRelativeDynamicLinks(
                testZipPath,
                sourceContractorFilePath: null,
                matchedPairs: new List<BoqMatchedPair>(),
                originalTemplateFormulas: null,
                knownContractorFilePaths: contractorFiles,
                knownTargetFilePaths: targetFiles,
                originalTemplateFilePath: @"D:\Tender\Docs\TestConsultantSummary.xlsx");

            // Verify the results in the zip archive
            using (var zip = System.IO.Compression.ZipFile.OpenRead(testZipPath))
            {
                var l1Entry = zip.GetEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
                Assert.NotNull(l1Entry);
                using (var r = new System.IO.StreamReader(l1Entry.Open()))
                {
                    string xml1 = r.ReadToEnd();
                    // Contractor file must stay as original file name without _Reconciled
                    Assert.Contains("Target=\"CustomVendorRates_2026.xlsx\"", xml1);
                    Assert.DoesNotContain("CustomVendorRates_2026_Reconciled.xlsx", xml1);
                }

                var l2Entry = zip.GetEntry("xl/externalLinks/_rels/externalLink2.xml.rels");
                Assert.NotNull(l2Entry);
                using (var r = new System.IO.StreamReader(l2Entry.Open()))
                {
                    string xml2 = r.ReadToEnd();
                    // Consultant sibling package must be converted to _Reconciled.xlsx
                    Assert.Contains("Target=\"Project_Civil_Package_Rev01_Reconciled.xlsx\"", xml2);
                }
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
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
