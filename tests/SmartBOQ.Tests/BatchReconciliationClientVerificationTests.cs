using System.Diagnostics;
using System.IO;
using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Export;
using SmartBOQ.Infrastructure.Matching;
using SmartBOQ.Infrastructure.Parsers;
using SmartBOQ.Infrastructure.Storage;
using SmartBOQ.Infrastructure.Verification;
using Xunit;
using Xunit.Abstractions;

namespace SmartBOQ.Tests;

public class BatchReconciliationClientVerificationTests
{
    private readonly ITestOutputHelper _output;

    public BatchReconciliationClientVerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ReconcileAllClientFiles_AchievesNear100PercentMatch()
    {
        string baseDir = @"C:\Users\BodyBoy\Desktop\BOQs";
        string sourceCandy = Path.Combine(baseDir, "CANDY FILE.xlsx");
        string sourceElec = Path.Combine(baseDir, "Electrical.xlsx");
        string sourceMech = Path.Combine(baseDir, "Mechanical.xlsx");

        if (!File.Exists(sourceCandy) || !File.Exists(sourceElec) || !File.Exists(sourceMech))
        {
            _output.WriteLine("Client BOQ files not found at expected path. Skipping local live verification.");
            return;
        }

        var sourceFiles = new[] { sourceCandy, sourceElec, sourceMech };
        string targetDir = Path.Combine(baseDir, "BOQs");
        var targetFiles = Directory.GetFiles(targetDir, "*.xlsx")
                                   .Where(f => !Path.GetFileName(f).StartsWith("~$") && !Path.GetFileName(f).Contains("_Reconciled"))
                                   .OrderBy(f => f)
                                   .ToList();

        var gate = new PreFlightVerificationGate();
        var flatReader = new UniversalAdaptiveBoqReader();
        var hierReader = new HierarchicalBoqReader();
        var matcher = new HybridWeightedMatcher();
        var exporter = new ClosedXmlExporter();
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_smartboq_{Guid.NewGuid():N}.db");
        var repo = new SqliteBoqRepository(tempDb);
        var inspector = new BoqInspectorService();
        var service = new BoqReconciliationService(gate, flatReader, hierReader, matcher, exporter, repo, inspector);

        int grandTotalItems = 0;
        int grandTotalPriced = 0;

        _output.WriteLine("==========================================================================");
        _output.WriteLine("       SMARTBOQ ENTERPRISE - LIVE C# 11-SCHEDULE VERIFICATION             ");
        _output.WriteLine("==========================================================================");

        foreach (var tFile in targetFiles)
        {
            var result = await service.ReconcileMultiSourceAsync(sourceFiles, tFile, sensitivity: 0.85);

            int pricedCount = result.MatchedPairs.Count(p => 
                (p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum) || 
                (p.IsApproved && p.Confidence != MatchConfidence.Unmatched && !p.IsVariationOrder));
            int totalCount = result.MatchedPairs.Count;
            double pct = totalCount > 0 ? (double)pricedCount / totalCount * 100.0 : 0.0;

            grandTotalItems += totalCount;
            grandTotalPriced += pricedCount;

            _output.WriteLine($" • {Path.GetFileName(tFile)}: {pricedCount}/{totalCount} ({pct:F1}%)");
            if (pct < 100.0)
            {
                var unpriced = result.MatchedPairs
                    .Where(p => !((p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum) || 
                                  (p.IsApproved && p.Confidence != MatchConfidence.Unmatched && !p.IsVariationOrder)))
                    .Take(15);
                foreach (var u in unpriced)
                {
                    _output.WriteLine($"    [UNPRICED] Row:{u.TargetItem.AnchorRowIndex} Code:{u.TargetItem.ItemCode} SN:{u.TargetItem.SerialNumber} OrigRate:{u.TargetItem.OriginalRate} Qty:{u.TargetItem.Quantity} Unit:{u.TargetItem.Unit} Desc:{u.TargetItem.Description[..Math.Min(50, u.TargetItem.Description.Length)]} Conf:{u.Confidence} Appr:{u.IsApproved}");
                }
            }
        }

        double grandPct = grandTotalItems > 0 ? (double)grandTotalPriced / grandTotalItems * 100.0 : 0.0;
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"GRAND TOTAL: {grandTotalPriced}/{grandTotalItems} ({grandPct:F1}%)");
        _output.WriteLine("==========================================================================");

        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }

        Assert.True(grandPct >= 99.0, $"Expected at least 99% overall match, but got {grandPct:F1}% ({grandTotalPriced}/{grandTotalItems})");
    }

    [Fact]
    public async Task ReconcileAllClientFiles_WithCognitiveAdaptiveBrain_Achieves100PercentMatch()
    {
        string baseDir = @"C:\Users\BodyBoy\Desktop\BOQs";
        string sourceCandy = Path.Combine(baseDir, "CANDY FILE.xlsx");
        string sourceElec = Path.Combine(baseDir, "Electrical.xlsx");
        string sourceMech = Path.Combine(baseDir, "Mechanical.xlsx");

        if (!File.Exists(sourceCandy) || !File.Exists(sourceElec) || !File.Exists(sourceMech))
        {
            _output.WriteLine("Client BOQ files not found at expected path. Skipping local live verification.");
            return;
        }

        var sourceFiles = new[] { sourceCandy, sourceElec, sourceMech };
        string targetDir = Path.Combine(baseDir, "BOQs");
        var targetFiles = Directory.GetFiles(targetDir, "*.xlsx")
                                   .Where(f => !Path.GetFileName(f).StartsWith("~$") && !Path.GetFileName(f).Contains("_Reconciled"))
                                   .OrderBy(f => f)
                                   .ToList();

        var gate = new PreFlightVerificationGate();
        var flatReader = new UniversalAdaptiveBoqReader();
        var hierReader = new HierarchicalBoqReader();
        var brain = new SmartBOQ.Infrastructure.CognitiveBrain.Engine.CognitiveAdaptiveBrain();
        var exporter = new ClosedXmlExporter();
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_smartboq_brain_{Guid.NewGuid():N}.db");
        var repo = new SqliteBoqRepository(tempDb);
        var inspector = new BoqInspectorService();
        var service = new BoqReconciliationService(gate, flatReader, hierReader, brain, exporter, repo, inspector);

        int grandTotalItems = 0;
        int grandTotalPriced = 0;
        decimal grandTotalFinancialAmount = 0m;

        _output.WriteLine("==========================================================================");
        _output.WriteLine("   COGNITIVE ADAPTIVE BRAIN - LIVE C# 11-SCHEDULE VERIFICATION            ");
        _output.WriteLine("==========================================================================");

        foreach (var tFile in targetFiles)
        {
            var result = await service.ReconcileMultiSourceAsync(sourceFiles, tFile, sensitivity: 0.85);

            int pricedCount = result.MatchedPairs.Count(p => 
                (p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum) || 
                (p.IsApproved && p.Confidence != MatchConfidence.Unmatched && !p.IsVariationOrder));
            int totalCount = result.MatchedPairs.Count;
            double pct = totalCount > 0 ? (double)pricedCount / totalCount * 100.0 : 0.0;

            decimal fileAmount = result.MatchedPairs
                .Where(p => p.InjectedRate.HasValue)
                .Sum(p => p.TargetItem.Quantity * p.InjectedRate!.Value);

            grandTotalItems += totalCount;
            grandTotalPriced += pricedCount;
            grandTotalFinancialAmount += fileAmount;

            _output.WriteLine($" • {Path.GetFileName(tFile)}: {pricedCount}/{totalCount} ({pct:F1}%) | Subtotal: {fileAmount:N0} EGP");
        }

        double grandPct = grandTotalItems > 0 ? (double)grandTotalPriced / grandTotalItems * 100.0 : 0.0;
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"COGNITIVE BRAIN GRAND TOTAL: {grandTotalPriced}/{grandTotalItems} ({grandPct:F1}%)");
        _output.WriteLine($"TOTAL RECONCILED FINANCIAL VALUE: {grandTotalFinancialAmount:N0} EGP");
        _output.WriteLine("==========================================================================");

        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }

        Assert.True(grandPct >= 99.0, $"Expected at least 99% overall match with Cognitive Brain, but got {grandPct:F1}%");
    }

    [Fact]
    public async Task ReconcileRasElHekma_DP3_Project_AchievesHighMatch()
    {
        string proDir = @"C:\Users\BodyBoy\Desktop\BOQs\pro";
        string sourceHatchway = Path.Combine(proDir, "DP3 - Hatchway.xlsx");
        string targetReh = Path.Combine(proDir, "REH.08.26.3199 DP3 Pricing Schedule Re-Measure.xlsx");

        if (!File.Exists(sourceHatchway) || !File.Exists(targetReh))
        {
            _output.WriteLine("Ras El Hekma DP3 files not found. Skipping test.");
            return;
        }

        var gate = new PreFlightVerificationGate();
        var flatReader = new UniversalAdaptiveBoqReader();
        var hierReader = new HierarchicalBoqReader();
        var brain = new SmartBOQ.Infrastructure.CognitiveBrain.Engine.CognitiveAdaptiveBrain();
        var exporter = new ClosedXmlExporter();
        string tempDb = Path.Combine(Path.GetTempPath(), $"test_smartboq_reh_{Guid.NewGuid():N}.db");
        var repo = new SqliteBoqRepository(tempDb);
        var inspector = new BoqInspectorService();
        var service = new BoqReconciliationService(gate, flatReader, hierReader, brain, exporter, repo, inspector);

        _output.WriteLine("==========================================================================");
        _output.WriteLine("      RAS EL HEKMA (DP3) - LIVE RECONCILIATION VERIFICATION               ");
        _output.WriteLine("==========================================================================");
        _output.WriteLine($" • Source File (Contractor): {Path.GetFileName(sourceHatchway)}");
        _output.WriteLine($" • Target File (Consultant): {Path.GetFileName(targetReh)}");

        var stopwatch = Stopwatch.StartNew();
        var result = await service.ReconcileAsync(sourceHatchway, targetReh, sensitivity: 0.85);
        stopwatch.Stop();

        int totalCount = result.MatchedPairs.Count;
        int pricedCount = result.MatchedPairs.Count(p => 
            (p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum) || 
            (p.IsApproved && p.Confidence != MatchConfidence.Unmatched && !p.IsVariationOrder));
        int exactCount = result.MatchedPairs.Count(p => p.Confidence == MatchConfidence.Exact);
        int fuzzyCount = result.MatchedPairs.Count(p => p.Confidence == MatchConfidence.HighFuzzy);
        int voCount = result.MatchedPairs.Count(p => p.IsVariationOrder);
        int psCount = result.MatchedPairs.Count(p => p.TargetItem.Type == BoqItemType.ProvisionalSum);

        double pct = totalCount > 0 ? (double)pricedCount / totalCount * 100.0 : 0.0;

        decimal totalReconciledAmount = result.MatchedPairs
            .Where(p => p.InjectedRate.HasValue)
            .Sum(p => p.TargetItem.Quantity * p.InjectedRate!.Value);

        _output.WriteLine("--------------------------------------------------------------------------");
        _output.WriteLine($" • Total Consultant Items  : {totalCount}");
        _output.WriteLine($" • Total Priced & Approved  : {pricedCount} ({pct:F1}%)");
        _output.WriteLine($" • Exact Matches            : {exactCount}");
        _output.WriteLine($" • High Fuzzy Matches       : {fuzzyCount}");
        _output.WriteLine($" • Provisional Sums Shielded: {psCount}");
        _output.WriteLine($" • Variation Orders (New)   : {voCount}");
        _output.WriteLine($" • Total Reconciled Value   : {totalReconciledAmount:N0} EGP");
        _output.WriteLine($" • Processing Duration      : {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine("==========================================================================");

        var unpriced = result.MatchedPairs.Where(p => p.IsVariationOrder).Take(20);
        _output.WriteLine("Sample of Unpriced / Variation Orders:");
        foreach (var u in unpriced)
        {
            _output.WriteLine($"   Sheet:{u.TargetItem.SheetName} Code:{u.TargetItem.ItemCode} Qty:{u.TargetItem.Quantity} Unit:{u.TargetItem.Unit} Desc:{u.TargetItem.Description[..Math.Min(60, u.TargetItem.Description.Length)]}");
        }

        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }

        Assert.True(pct >= 80.0, $"Expected at least 80% match on Ras El Hekma, got {pct:F1}%");
    }
}
