using System.IO;
using ClosedXML.Excel;
using ExcelDataReader;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;
using SmartBOQ.Infrastructure.Export;
using SmartBOQ.Infrastructure.Parsers;
using Xunit;

namespace SmartBOQ.Tests;

public class InfrastructureUnitTests
{
    [Fact]
    public void CompactStringPool_ReturnsIdenticalInstanceForDuplicates()
    {
        var pool = new CompactStringPool();

        string s1 = pool.GetOrAdd("Reinforced Concrete Grade C40");
        string s2 = pool.GetOrAdd("Reinforced Concrete Grade C40");

        Assert.Same(s1, s2);
    }

    [Fact]
    public void CompactStringPool_ConcurrentAccess_IsThreadSafe()
    {
        var pool = new CompactStringPool();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 1000, i =>
        {
            try
            {
                string key = $"Item_{(i % 20)}";
                string res = pool.GetOrAdd(key);
                Assert.NotNull(res);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    [Fact]
    public void SpanTokenizer_HashToken_IsCaseInsensitive()
    {
        ulong h1 = SpanTokenizer.HashToken("excavation".AsSpan());
        ulong h2 = SpanTokenizer.HashToken("EXCAVATION".AsSpan());
        ulong h3 = SpanTokenizer.HashToken("Excavation".AsSpan());

        Assert.Equal(h1, h2);
        Assert.Equal(h2, h3);
    }

    [Fact]
    public void SpanTokenizer_ExtractSortedTokenHashes_FiltersNoiseAndStripsAlPrefix()
    {
        var hashes1 = SpanTokenizer.ExtractSortedTokenHashes("أعمال الخرسانة المسلحة".AsSpan());
        var hashes2 = SpanTokenizer.ExtractSortedTokenHashes("خرسانة مسلحة".AsSpan());

        // Both should share token hashes for reinforced concrete keywords despite prefixes and noise words
        double jaccard = SpanTokenizer.CalculateSortedJaccard(hashes1, hashes2);
        Assert.True(jaccard > 0.40);
    }

    [Fact]
    public void SpanTokenizer_CalculateSortedJaccard_IdenticalTokensReturnsOne()
    {
        ulong[] tokens = [100UL, 200UL, 300UL];
        double jaccard = SpanTokenizer.CalculateSortedJaccard(tokens, tokens);

        Assert.Equal(1.0, jaccard);
    }

    [Fact]
    public void SpanTokenizer_CalculateSortedJaccard_DisjointTokensReturnsZero()
    {
        ulong[] tokensA = [100UL, 200UL];
        ulong[] tokensB = [300UL, 400UL];
        double jaccard = SpanTokenizer.CalculateSortedJaccard(tokensA, tokensB);

        Assert.Equal(0.0, jaccard);
    }

    [Fact]
    public void SpanTokenizer_BitParallelDistance_CalculatesAccurateLevenshtein()
    {
        int dist1 = SpanTokenizer.BitParallelDistance("kitten".AsSpan(), "sitting".AsSpan());
        Assert.Equal(3, dist1);

        int dist2 = SpanTokenizer.BitParallelDistance("same".AsSpan(), "same".AsSpan());
        Assert.Equal(0, dist2);
    }

    [Theory]
    [InlineData("01 - Earthworks", "01")]
    [InlineData("Bill 02A - 3BR Villa East", "02A")]
    [InlineData("Bill 06.1J - 6Plex TH East PS", "06.1J")]
    [InlineData("الباب الأول - أعمال الحفر", "1")]
    [InlineData("الباب الثاني - خرسانات مسلحة", "2")]
    [InlineData("الباب السادس - مبالغ احتياطية", "6")]
    [InlineData("الباب 1 - أعمال الحفر", "1")]
    [InlineData("الباب 2 - خرسانات", "2")]
    public void BoqInspectorService_ExtractBillCode_ExtractsCanonicalIdentifiers(string sheetName, string expectedCode)
    {
        string extracted = BoqInspectorService.ExtractBillCode(sheetName);
        Assert.Equal(expectedCode, extracted);
    }

    [Fact]
    public async Task BoqInspectorService_AutoLinkSheetsAsync_LinksNumericAndOrdinalArabicSheets()
    {
        var inspector = new BoqInspectorService();

        var targetSheets = new List<BoqSheetSummary>
        {
            new() { SheetName = "الباب الأول - أعمال الحفر", BillCode = "1", EstimatedRows = 10, SourceFileName = "Tgt.xlsx" },
            new() { SheetName = "الباب الثاني - خرسانات مسلحة", BillCode = "2", EstimatedRows = 20, SourceFileName = "Tgt.xlsx" },
            new() { SheetName = "الباب السادس - مبالغ احتياطية", BillCode = "6", EstimatedRows = 1, IsProvisionalSum = true, SourceFileName = "Tgt.xlsx" }
        };

        var sourceSheets = new List<BoqSheetSummary>
        {
            new() { SheetName = "الباب 1 - أعمال الحفر", BillCode = "1", EstimatedRows = 10, SourceFileName = "Src.xlsx" },
            new() { SheetName = "الباب 2 - خرسانات", BillCode = "2", EstimatedRows = 20, SourceFileName = "Src.xlsx" }
        };

        var links = await inspector.AutoLinkSheetsAsync(targetSheets, sourceSheets);

        Assert.Equal(3, links.Count);

        var link1 = links.First(l => l.TargetSheetName.Contains("الأول"));
        Assert.Equal("الباب 1 - أعمال الحفر", link1.SelectedSourceSheet);
        Assert.Equal(SheetLinkStatus.AutoMatched, link1.Status);

        var link2 = links.First(l => l.TargetSheetName.Contains("الثاني"));
        Assert.Equal("الباب 2 - خرسانات", link2.SelectedSourceSheet);
        Assert.Equal(SheetLinkStatus.AutoMatched, link2.Status);

        var link6 = links.First(l => l.TargetSheetName.Contains("السادس"));
        Assert.True(link6.IsProvisionalSum);
        Assert.Equal(SheetLinkStatus.ShieldedPS, link6.Status);
    }

    [Fact]
    public void SemanticColumnResolver_PrioritizesCompositeRateOverSplitRates()
    {
        // Tests intelligent routing when contractor sheet splits rates into Supply and Install plus Total Unit Rate
        var scannedRows = new List<string[]>
        {
            new[] { "Item", "Description", "Unit", "Qty", "Supply Rate", "Install Rate", "Total Unit Rate", "Total Amount" },
            new[] { "1", "High-pressure valve supply and install", "item", "10", "450.00", "50.00", "500.00", "5000.00" }
        };

        var resolved = SemanticColumnResolver.ResolveColumnsFromRows(scannedRows, 8);

        Assert.Equal(0, resolved.ItemCodeColumn);
        Assert.Equal(1, resolved.DescriptionColumn);
        Assert.Equal(2, resolved.UnitColumn);
        Assert.Equal(3, resolved.QuantityColumn);
        Assert.Equal(6, resolved.RateColumn);        // Must pick "Total Unit Rate" (Col 6), not Supply Rate (Col 4)
        Assert.Equal(7, resolved.TotalAmountColumn); // Must pick "Total Amount" (Col 7)
    }

    [Theory]
    [InlineData("Bill 01 - Summary of Earthworks", false)]
    [InlineData("جدول 02 - ملخص أعمال البنية التحتية", false)]
    [InlineData("Package A - Summary of Concrete", false)]
    [InlineData("Grand Summary", true)]
    [InlineData("ملخص عام للمشروع", true)]
    [InlineData("Table of Contents", true)]
    public void BaseBoqReader_IsNonBillSheet_PreservesActiveBillsWithSummaryNames(string sheetName, bool expectedIsNonBill)
    {
        var method = typeof(BaseBoqReader).GetMethod("IsNonBillSheet", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        bool actual = (bool)method.Invoke(null, new object[] { sheetName })!;
        Assert.Equal(expectedIsNonBill, actual);
    }

    [Fact]
    public void ExportStandaloneDashboard_ProducesZeroRefErrors_AndValidExternalLinks()
    {
        string tempDashPath = Path.Combine(Path.GetTempPath(), $"Test_Dashboard_{Guid.NewGuid():N}.xlsx");
        try
        {
            var targetItem = new BoqItem
            {
                Id = "T1",
                ItemCode = "CIV-001",
                Description = "Excavation in all types of soil",
                Unit = "m3",
                Quantity = 500m,
                UnitRate = 0m,
                SheetName = "Civil Works",
                BillNumber = "Bill 01",
                AnchorRowIndex = 15,
                WorkbookName = "C:\\Projects\\01_Civil_Package.xlsx",
                RateColumnIndex = 7,
                QuantityColumnIndex = 5,
                AmountColumnIndex = 8
            };

            var sourceItem = new BoqItem
            {
                Id = "S1",
                ItemCode = "SRC-101",
                Description = "Excavation and disposal",
                Unit = "m3",
                Quantity = 500m,
                UnitRate = 120.50m,
                SheetName = "Rates Master",
                BillNumber = "General",
                AnchorRowIndex = 42,
                WorkbookName = "C:\\Projects\\Contractor_Rates.xlsx",
                RateColumnIndex = 18
            };

            var pair = new BoqMatchedPair
            {
                TargetItem = targetItem,
                MatchedSourceItem = sourceItem,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                InjectedRate = 120.50m,
                MatchRationale = "Exact Semantic Match",
                IsApproved = true
            };
            var pairs = new List<BoqMatchedPair> { pair };

            ClosedXmlExporter.ExportStandaloneDashboard(tempDashPath, pairs, "C:\\Projects\\Contractor_Rates.xlsx");

            using var wb = new XLWorkbook(tempDashPath);
            var auditWs = wb.Worksheet("Audit_Report");
            Assert.NotNull(auditWs);

            // Row 8 is first data row (headers are rows 6-7)
            var tenderJumpCell = auditWs.Cell(8, 1);
            Assert.Contains("01_Civil_Package_Reconciled.xlsx", tenderJumpCell.FormulaA1);
            Assert.Contains("'Civil Works'!G15", tenderJumpCell.FormulaA1);
            Assert.DoesNotContain("#REF!", tenderJumpCell.FormulaA1);

            var qtyCell = auditWs.Cell(8, 10);
            Assert.False(qtyCell.HasFormula); // Standalone dashboard writes direct numeric quantity to prevent #REF!
            Assert.Equal(500.0, qtyCell.GetDouble());

            var rateCell = auditWs.Cell(8, 12);
            Assert.DoesNotContain("#REF!", rateCell.FormulaA1);
            Assert.Contains("01_Civil_Package_Reconciled.xlsx", rateCell.FormulaA1);

            var amtCell = auditWs.Cell(8, 13);
            Assert.Equal("J8*L8", amtCell.FormulaA1); // Local dynamic quantity*rate formula

            var mapWs = wb.Worksheet("Pricing_Linkage_Map");
            Assert.NotNull(mapWs);
            var mapQtyCell = mapWs.Cell(8, 8);
            Assert.False(mapQtyCell.HasFormula);
            Assert.Equal(500.0, mapQtyCell.GetDouble());
            var mapRateCell = mapWs.Cell(8, 15);
            Assert.False(mapRateCell.HasFormula);
            Assert.Equal(120.50, mapRateCell.GetDouble());
        }
        finally
        {
            if (File.Exists(tempDashPath)) File.Delete(tempDashPath);
        }
    }

    [Fact]
    public void InjectRelativeDynamicLinks_HandlesMultipleSheetsAndSpacesInContractorFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"TestDynamicLink_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Create a dummy contractor workbook with two sheets including spaces
            string contractorFile = Path.Combine(tempDir, "Contractor Rates.xlsx");
            using (var wbContractor = new XLWorkbook())
            {
                wbContractor.Worksheets.Add("Index Sheet");
                wbContractor.Worksheets.Add("Phase 1 & 2 Rates");
                wbContractor.SaveAs(contractorFile);
            }

            // 2. Create a dummy target tender package
            string targetPackage = Path.Combine(tempDir, "Package_01_Reconciled.xlsx");
            using (var wbTarget = new XLWorkbook())
            {
                var ws = wbTarget.Worksheets.Add("Bill_Civil");
                ws.Cell("G10").Value = 0;
                wbTarget.SaveAs(targetPackage);
            }

            var targetItem = new BoqItem
            {
                Id = "T1",
                BillNumber = "Bill 01",
                Description = "Target item",
                SheetName = "Bill_Civil",
                AnchorRowIndex = 10,
                RateColumnIndex = 7
            };

            var sourceItem = new BoqItem
            {
                Id = "S1",
                BillNumber = "Bill 01",
                Description = "Source item",
                WorkbookName = "Contractor Rates.xlsx",
                SheetName = "Phase 1 & 2 Rates",
                AnchorRowIndex = 25,
                RateColumnIndex = 18,
                UnitRate = 250m
            };

            var pair = new BoqMatchedPair
            {
                TargetItem = targetItem,
                MatchedSourceItem = sourceItem,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                InjectedRate = 250m,
                IsApproved = true
            };
            var pairs = new List<BoqMatchedPair> { pair };

            ClosedXmlExporter.InjectRelativeDynamicLinks(targetPackage, "Contractor Rates.xlsx", pairs);

            // Inspect the target package's externalLink1.xml
            using var zip = System.IO.Compression.ZipFile.OpenRead(targetPackage);
            var extEntry = zip.GetEntry("xl/externalLinks/externalLink1.xml");
            Assert.NotNull(extEntry);

            using var sr = new StreamReader(extEntry.Open());
            string extXml = sr.ReadToEnd();

            // Must contain both sheets from the contractor workbook in exact order
            Assert.Contains("Index Sheet", extXml);
            Assert.Contains("Phase 1 &amp; 2 Rates", extXml);

            // Inspect the sheet XML to verify quoted external formula syntax
            var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
            Assert.NotNull(sheetEntry);
            using var srSheet = new StreamReader(sheetEntry.Open());
            string sheetXml = srSheet.ReadToEnd();

            Assert.Contains("'[1]Phase 1 & 2 Rates'!$R$25", sheetXml);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void InjectRelativeDynamicLinks_PreservesExistingSheetNames_WhenExternalFileNotYetCreated()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"TestPreserve_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Create a dummy package that already has an externalLink referencing "Summary" sheet
            string targetPackage = Path.Combine(tempDir, "Grand_Summary_Reconciled.xlsx");
            using (var wbTarget = new XLWorkbook())
            {
                var ws = wbTarget.Worksheets.Add("Grand Main");
                ws.Cell("F14").Value = 100;
                wbTarget.SaveAs(targetPackage);
            }

            // Manually inject an external link with "PRELIMINARIES-BOQ" and "Summary"
            using (var zip = System.IO.Compression.ZipFile.Open(targetPackage, System.IO.Compression.ZipArchiveMode.Update))
            {
                var extEntry = zip.CreateEntry("xl/externalLinks/externalLink1.xml");
                using var sw = new StreamWriter(extEntry.Open());
                sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                         "<externalLink xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\r\n" +
                         "  <externalBook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId1\">\r\n" +
                         "    <sheetNames><sheetName val=\"PRELIMINARIES-BOQ\"/><sheetName val=\"Summary\"/></sheetNames>\r\n" +
                         "  </externalBook>\r\n" +
                         "</externalLink>");

                var relsEntry = zip.CreateEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
                using var swRels = new StreamWriter(relsEntry.Open());
                swRels.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                             "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                             "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath\" Target=\"01_Preliminaries.xlsx\" TargetMode=\"External\"/>\r\n" +
                             "</Relationships>");
            }

            var pairs = new List<BoqMatchedPair>();

            // Run injection without the physical "01_Preliminaries.xlsx" being present on disk
            ClosedXmlExporter.InjectRelativeDynamicLinks(targetPackage, "Contractor.xlsx", pairs);

            // Verify that the existing sheet names PRELIMINARIES-BOQ and Summary were NOT wiped out or replaced by Sheet1
            using (var zip = System.IO.Compression.ZipFile.OpenRead(targetPackage))
            {
                var extEntry = zip.GetEntry("xl/externalLinks/externalLink1.xml");
                Assert.NotNull(extEntry);
                using var sr = new StreamReader(extEntry.Open());
                string extXml = sr.ReadToEnd();
                Assert.Contains("PRELIMINARIES-BOQ", extXml);
                Assert.Contains("Summary", extXml);
                Assert.DoesNotContain("<sheetName val=\"Sheet1\"/>", extXml);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
