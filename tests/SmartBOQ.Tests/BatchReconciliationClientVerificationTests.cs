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
}
