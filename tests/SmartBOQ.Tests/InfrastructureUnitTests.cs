using ClosedXML.Excel;
using ExcelDataReader;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;
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
}
