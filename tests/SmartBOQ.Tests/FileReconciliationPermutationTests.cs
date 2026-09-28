using System.IO;
using ClosedXML.Excel;
using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Export;
using SmartBOQ.Infrastructure.Matching;
using SmartBOQ.Infrastructure.Parsers;
using SmartBOQ.Infrastructure.Storage;
using SmartBOQ.Infrastructure.Verification;
using Xunit;

namespace SmartBOQ.Tests;

public class FileReconciliationPermutationTests : IDisposable
{
    private readonly string _tempDir;

    public FileReconciliationPermutationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SmartBOQ_PermutationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup of temp files
        }
    }

    private BoqReconciliationService CreateReconciliationService(string dbName)
    {
        var gate = new PreFlightVerificationGate();
        var flatR = new HatchwayFlatReader();
        var hierR = new HierarchicalBoqReader();
        var matcher = new HybridWeightedMatcher();
        var exporter = new ClosedXmlExporter();
        var repo = new SqliteBoqRepository(Path.Combine(_tempDir, dbName));
        var inspector = new BoqInspectorService();
        return new BoqReconciliationService(gate, flatR, hierR, matcher, exporter, repo, inspector);
    }

    [Fact]
    public async Task Permutation1_ScrambledColumns_AutoRoutesAndInjectsRatesWithoutBrokenFormulas()
    {
        string tgtFile = Path.Combine(_tempDir, "P1_Target.xlsx");
        string srcFile = Path.Combine(_tempDir, "P1_Source_Scrambled.xlsx");
        string outFile = Path.Combine(_tempDir, "P1_Out.xlsx");

        // Target: Col A: Code, Col C: Desc, Col E: Qty, Col F: Unit, Col G: Rate, Col H: Amount
        using (var wbT = new XLWorkbook())
        {
            var ws = wbT.Worksheets.Add("Bill 01 - Earthworks");
            ws.Cell(1, 1).Value = "Item"; ws.Cell(1, 3).Value = "Description"; ws.Cell(1, 5).Value = "Quantity"; ws.Cell(1, 6).Value = "Unit"; ws.Cell(1, 7).Value = "Rate"; ws.Cell(1, 8).Value = "Total";
            ws.Cell(2, 1).Value = "A"; ws.Cell(2, 3).Value = "Bulk earthworks excavation in sand"; ws.Cell(2, 5).Value = 5000; ws.Cell(2, 6).Value = "m3"; ws.Cell(2, 8).FormulaA1 = "E2*G2";
            ws.Cell(3, 1).Value = "B"; ws.Cell(3, 3).Value = "Backfilling with approved granular material"; ws.Cell(3, 5).Value = 2200; ws.Cell(3, 6).Value = "m3"; ws.Cell(3, 8).FormulaA1 = "E3*G3";
            wbT.SaveAs(tgtFile);
        }

        // Source: Scrambled order: Col A: Qty, Col B: Rate, Col C: Code, Col D: Unit, Col E: Description, Col F: Amount
        using (var wbS = new XLWorkbook())
        {
            var ws = wbS.Worksheets.Add("Bill 01 - Earthworks");
            ws.Cell(1, 1).Value = "Quantity"; ws.Cell(1, 2).Value = "Unit Rate"; ws.Cell(1, 3).Value = "Item Code"; ws.Cell(1, 4).Value = "Unit"; ws.Cell(1, 5).Value = "Description"; ws.Cell(1, 6).Value = "Total Amount";
            ws.Cell(2, 1).Value = 5000; ws.Cell(2, 2).Value = 18.50; ws.Cell(2, 3).Value = "A"; ws.Cell(2, 4).Value = "m3"; ws.Cell(2, 5).Value = "Bulk earthworks excavation in sand"; ws.Cell(2, 6).Value = 92500;
            ws.Cell(3, 1).Value = 2200; ws.Cell(3, 2).Value = 24.00; ws.Cell(3, 3).Value = "B"; ws.Cell(3, 4).Value = "m3"; ws.Cell(3, 5).Value = "Backfilling with approved granular material"; ws.Cell(3, 6).Value = 52800;
            wbS.SaveAs(srcFile);
        }

        var service = CreateReconciliationService("p1.db");
        var result = await service.ReconcileAsync(srcFile, tgtFile, 0.85);
        await service.ExportPricedScheduleAsync(tgtFile, outFile, result.MatchedPairs, srcFile, enableDynamicLinking: true);

        // Verification
        using var wbCheck = new XLWorkbook(outFile);
        var wsCheck = wbCheck.Worksheet("Bill 01 - Earthworks");

        Assert.Equal(18.50, wsCheck.Cell(2, 7).GetDouble(), 2);
        Assert.Equal(24.00, wsCheck.Cell(3, 7).GetDouble(), 2);

        // Verify formulas in Column H are 100% intact and not broken
        Assert.True(wsCheck.Cell(2, 8).HasFormula);
        Assert.DoesNotContain("#REF", wsCheck.Cell(2, 8).FormulaA1);
        Assert.DoesNotContain("#VALUE", wsCheck.Cell(2, 8).FormulaA1);
        Assert.True(wsCheck.Cell(3, 8).HasFormula);
        Assert.DoesNotContain("#REF", wsCheck.Cell(3, 8).FormulaA1);
        Assert.DoesNotContain("#VALUE", wsCheck.Cell(3, 8).FormulaA1);
    }

    [Fact]
    public async Task Permutation2_ArabicBilingualBOQ_OrdinalTitles_MatchesAndShieldsProvisionalSums()
    {
        string tgtFile = Path.Combine(_tempDir, "P2_Target_Arabic.xlsx");
        string srcFile = Path.Combine(_tempDir, "P2_Source_Arabic.xlsx");
        string outFile = Path.Combine(_tempDir, "P2_Out_Arabic.xlsx");

        using (var wbT = new XLWorkbook())
        {
            var ws1 = wbT.Worksheets.Add("الباب الأول - أعمال الحفر");
            ws1.Cell(1, 1).Value = "كود"; ws1.Cell(1, 3).Value = "بيان الأعمال"; ws1.Cell(1, 5).Value = "الكمية"; ws1.Cell(1, 6).Value = "الوحدة"; ws1.Cell(1, 7).Value = "الفئة"; ws1.Cell(1, 8).Value = "الإجمالي";
            ws1.Cell(2, 1).Value = "1.1"; ws1.Cell(2, 3).Value = "حفر في جميع أنواع التربة حتى مناسيب التأسيس مع نقل ناتج الحفر للمقالب العمومية المعتمدة"; ws1.Cell(2, 5).Value = 1500; ws1.Cell(2, 6).Value = "م3"; ws1.Cell(2, 8).FormulaA1 = "E2*G2";

            var ws2 = wbT.Worksheets.Add("الباب السادس - مبالغ احتياطية");
            ws2.Cell(1, 1).Value = "كود"; ws2.Cell(1, 3).Value = "بيان الأعمال"; ws2.Cell(1, 5).Value = "الكمية"; ws2.Cell(1, 6).Value = "الوحدة"; ws2.Cell(1, 7).Value = "الفئة"; ws2.Cell(1, 8).Value = "الإجمالي";
            ws2.Cell(2, 1).Value = "6.1"; ws2.Cell(2, 3).Value = "مبلغ احتياطي معتمد لأعمال الطوارئ"; ws2.Cell(2, 5).Value = 1; ws2.Cell(2, 6).Value = "مقطوعية"; ws2.Cell(2, 7).Value = 250000; ws2.Cell(2, 8).Value = 250000;
            wbT.SaveAs(tgtFile);
        }

        using (var wbS = new XLWorkbook())
        {
            var ws1 = wbS.Worksheets.Add("الباب 1 - أعمال الحفر");
            ws1.Cell(1, 1).Value = "كود"; ws1.Cell(1, 2).Value = "البيان"; ws1.Cell(1, 3).Value = "الوحدة"; ws1.Cell(1, 4).Value = "الكمية"; ws1.Cell(1, 5).Value = "سعر الوحدة";
            ws1.Cell(2, 1).Value = "1.1"; ws1.Cell(2, 2).Value = "حفر في جميع أنواع التربة الرملية حتى مناسيب التأسيس ونقل ناتج الحفر للمقالب العمومية"; ws1.Cell(2, 3).Value = "م3"; ws1.Cell(2, 4).Value = 1500; ws1.Cell(2, 5).Value = 95.00;

            var ws2 = wbS.Worksheets.Add("مبالغ احتياطية");
            ws2.Cell(1, 1).Value = "كود"; ws2.Cell(1, 2).Value = "البيان"; ws2.Cell(1, 5).Value = "سعر الوحدة";
            ws2.Cell(2, 1).Value = "6.1"; ws2.Cell(2, 2).Value = "مبلغ احتياطي معتمد لأعمال الطوارئ"; ws2.Cell(2, 5).Value = 180000; // Contractor proposed different rate
            wbS.SaveAs(srcFile);
        }

        var service = CreateReconciliationService("p2.db");
        var result = await service.ReconcileAsync(srcFile, tgtFile, 0.85);
        await service.ExportPricedScheduleAsync(tgtFile, outFile, result.MatchedPairs, srcFile, enableDynamicLinking: true);

        using var wbCheck = new XLWorkbook(outFile);
        var ws1Check = wbCheck.Worksheet("الباب الأول - أعمال الحفر");
        Assert.Equal(95.00, ws1Check.Cell(2, 7).GetDouble(), 2);

        // Verify Provisional Sum in target was SHIELDED (not overwritten by contractor's 180,000)
        var ws2Check = wbCheck.Worksheet("الباب السادس - مبالغ احتياطية");
        Assert.Equal(250000, ws2Check.Cell(2, 7).GetDouble(), 2);

        // Verify Cultural Numeral Audit (Strictly ASCII digits '0-9', 0 Hindi digits)
        if (wbCheck.TryGetWorksheet("Audit_Report", out var wsAudit))
        {
            var rxHindi = new System.Text.RegularExpressions.Regex(@"[\u0660-\u0669]");
            foreach (var cell in wsAudit.CellsUsed())
            {
                Assert.DoesNotMatch(rxHindi, cell.GetString());
            }
        }
    }

    [Fact]
    public async Task Permutation3_NamingNoiseAndDecimalPermutations_LinksSheetsAccurately()
    {
        string tgtFile = Path.Combine(_tempDir, "P3_Target_Complex.xlsx");
        string srcFile = Path.Combine(_tempDir, "P3_Source_Noise.xlsx");
        string outFile = Path.Combine(_tempDir, "P3_Out.xlsx");

        using (var wbT = new XLWorkbook())
        {
            var wsT1 = wbT.Worksheets.Add("Bill 02A - 3BR Villa East");
            wsT1.Cell(1, 1).Value = "Code"; wsT1.Cell(1, 3).Value = "Description"; wsT1.Cell(1, 5).Value = "Qty"; wsT1.Cell(1, 6).Value = "Unit"; wsT1.Cell(1, 7).Value = "Rate"; wsT1.Cell(1, 8).Value = "Amount";
            wsT1.Cell(2, 1).Value = "1"; wsT1.Cell(2, 3).Value = "Ceramic floor tiles 60x60cm for living rooms"; wsT1.Cell(2, 5).Value = 1200; wsT1.Cell(2, 6).Value = "m2"; wsT1.Cell(2, 8).FormulaA1 = "E2*G2";

            var wsT2 = wbT.Worksheets.Add("Bill 06.1J - 6Plex TH East PS");
            wsT2.Cell(1, 1).Value = "Code"; wsT2.Cell(1, 3).Value = "Description"; wsT2.Cell(1, 5).Value = "Qty"; wsT2.Cell(1, 6).Value = "Unit"; wsT2.Cell(1, 7).Value = "Rate"; wsT2.Cell(1, 8).Value = "Amount";
            wsT2.Cell(2, 1).Value = "A"; wsT2.Cell(2, 3).Value = "50mm thick sand cement screed to receive paving"; wsT2.Cell(2, 5).Value = 680; wsT2.Cell(2, 6).Value = "m2"; wsT2.Cell(2, 8).FormulaA1 = "E2*G2";
            wbT.SaveAs(tgtFile);
        }

        using (var wbS = new XLWorkbook())
        {
            var wsS1 = wbS.Worksheets.Add("02A_3BR_Villa_East");
            wsS1.Cell(1, 1).Value = "Item"; wsS1.Cell(1, 2).Value = "Description"; wsS1.Cell(1, 3).Value = "Unit"; wsS1.Cell(1, 4).Value = "Qty"; wsS1.Cell(1, 5).Value = "Unit Rate";
            wsS1.Cell(2, 1).Value = "1"; wsS1.Cell(2, 2).Value = "Ceramic floor tiles 60x60cm for living rooms"; wsS1.Cell(2, 3).Value = "m2"; wsS1.Cell(2, 4).Value = 1200; wsS1.Cell(2, 5).Value = 48.00;

            var wsS2 = wbS.Worksheets.Add("Bill 6.1J-Townhouse East PS");
            wsS2.Cell(1, 1).Value = "Item"; wsS2.Cell(1, 2).Value = "Description"; wsS2.Cell(1, 3).Value = "Unit"; wsS2.Cell(1, 4).Value = "Qty"; wsS2.Cell(1, 5).Value = "Unit Rate";
            wsS2.Cell(2, 1).Value = "A"; wsS2.Cell(2, 2).Value = "50mm thick sand cement screed to receive paving"; wsS2.Cell(2, 3).Value = "m2"; wsS2.Cell(2, 4).Value = 680; wsS2.Cell(2, 5).Value = 35.00;
            wbS.SaveAs(srcFile);
        }

        var service = CreateReconciliationService("p3.db");
        var result = await service.ReconcileAsync(srcFile, tgtFile, 0.85);
        await service.ExportPricedScheduleAsync(tgtFile, outFile, result.MatchedPairs, srcFile, enableDynamicLinking: true);

        using var wbCheck = new XLWorkbook(outFile);
        var wsCheck1 = wbCheck.Worksheet("Bill 02A - 3BR Villa East");
        Assert.Equal(48.00, wsCheck1.Cell(2, 7).GetDouble(), 2);

        var wsCheck2 = wbCheck.Worksheet("Bill 06.1J - 6Plex TH East PS");
        Assert.Equal(35.00, wsCheck2.Cell(2, 7).GetDouble(), 2);
    }

    [Fact]
    public async Task Permutation4_MultiContractorSources_MergesDistinctTradesIntoOneSchedule()
    {
        string tgtFile = Path.Combine(_tempDir, "P4_Target_Combined.xlsx");
        string srcEarthworks = Path.Combine(_tempDir, "P4_Source_SubContractor1.xlsx");
        string srcConcrete = Path.Combine(_tempDir, "P4_Source_SubContractor2.xlsx");
        string outFile = Path.Combine(_tempDir, "P4_Out_Merged.xlsx");

        // Consultant Tender contains both Earthworks and Concrete
        using (var wbT = new XLWorkbook())
        {
            var ws1 = wbT.Worksheets.Add("Bill 01 - Earthworks");
            ws1.Cell(1, 1).Value = "Code"; ws1.Cell(1, 3).Value = "Description"; ws1.Cell(1, 5).Value = "Qty"; ws1.Cell(1, 6).Value = "Unit"; ws1.Cell(1, 7).Value = "Rate"; ws1.Cell(1, 8).Value = "Amount";
            ws1.Cell(2, 1).Value = "A"; ws1.Cell(2, 3).Value = "Site clearance and tree removal"; ws1.Cell(2, 5).Value = 2500; ws1.Cell(2, 6).Value = "m2"; ws1.Cell(2, 8).FormulaA1 = "E2*G2";

            var ws2 = wbT.Worksheets.Add("Bill 02 - Concrete Substructure");
            ws2.Cell(1, 1).Value = "Code"; ws2.Cell(1, 3).Value = "Description"; ws2.Cell(1, 5).Value = "Qty"; ws2.Cell(1, 6).Value = "Unit"; ws2.Cell(1, 7).Value = "Rate"; ws2.Cell(1, 8).Value = "Amount";
            ws2.Cell(2, 1).Value = "A"; ws2.Cell(2, 3).Value = "Reinforced concrete raft slab Grade C40"; ws2.Cell(2, 5).Value = 1800; ws2.Cell(2, 6).Value = "m3"; ws2.Cell(2, 8).FormulaA1 = "E2*G2";
            wbT.SaveAs(tgtFile);
        }

        // Subcontractor 1 (Earthworks only)
        using (var wbS1 = new XLWorkbook())
        {
            var ws = wbS1.Worksheets.Add("Bill 01 - Earthworks");
            ws.Cell(1, 1).Value = "Code"; ws.Cell(1, 2).Value = "Description"; ws.Cell(1, 3).Value = "Unit"; ws.Cell(1, 4).Value = "Qty"; ws.Cell(1, 5).Value = "Rate";
            ws.Cell(2, 1).Value = "A"; ws.Cell(2, 2).Value = "Site clearance and tree removal"; ws.Cell(2, 3).Value = "m2"; ws.Cell(2, 4).Value = 2500; ws.Cell(2, 5).Value = 12.00;
            wbS1.SaveAs(srcEarthworks);
        }

        // Subcontractor 2 (Concrete only)
        using (var wbS2 = new XLWorkbook())
        {
            var ws = wbS2.Worksheets.Add("Bill 02 - Concrete Substructure");
            ws.Cell(1, 1).Value = "Code"; ws.Cell(1, 2).Value = "Description"; ws.Cell(1, 3).Value = "Unit"; ws.Cell(1, 4).Value = "Qty"; ws.Cell(1, 5).Value = "Rate";
            ws.Cell(2, 1).Value = "A"; ws.Cell(2, 2).Value = "Reinforced concrete raft slab Grade C40"; ws.Cell(2, 3).Value = "m3"; ws.Cell(2, 4).Value = 1800; ws.Cell(2, 5).Value = 350.00;
            wbS2.SaveAs(srcConcrete);
        }

        var service = CreateReconciliationService("p4.db");
        var inspector = new BoqInspectorService();
        var info1 = await inspector.InspectWorkbookAsync(srcEarthworks, BoqFileRole.ContractorPriced);
        var info2 = await inspector.InspectWorkbookAsync(srcConcrete, BoqFileRole.ContractorPriced);
        var infoTgt = await inspector.InspectWorkbookAsync(tgtFile, BoqFileRole.ConsultantTarget);

        var allSourceSummaries = info1.Sheets.Concat(info2.Sheets).ToList();
        var links = await inspector.AutoLinkSheetsAsync(infoTgt.Sheets, allSourceSummaries);

        var result = await service.ReconcileMultiSourceAsync(
            new[] { srcEarthworks, srcConcrete },
            tgtFile,
            0.85,
            links,
            columnMappings: null);

        await service.ExportPricedScheduleAsync(tgtFile, outFile, result.MatchedPairs, srcEarthworks, enableDynamicLinking: true);

        using var wbCheck = new XLWorkbook(outFile);
        var wsEarth = wbCheck.Worksheet("Bill 01 - Earthworks");
        Assert.Equal(12.00, wsEarth.Cell(2, 7).GetDouble(), 2);

        var wsConcrete = wbCheck.Worksheet("Bill 02 - Concrete Substructure");
        Assert.Equal(350.00, wsConcrete.Cell(2, 7).GetDouble(), 2);
    }
}
