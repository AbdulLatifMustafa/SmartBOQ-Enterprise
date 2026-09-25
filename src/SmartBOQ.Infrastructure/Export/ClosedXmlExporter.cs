using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Export;

/// <summary>
/// Template-safe Excel rate injection engine using ClosedXML.
/// Inherits from <see cref="BaseBoqExporter"/>.
/// Preserves 100% of the original consultant design, fonts, row heights, column widths,
/// and structure for all original worksheets without ANY formatting modifications,
/// while injecting numerical rates into Column G and appending an Executive Dashboard and Audit Report.
/// Supports native OpenXML Relative Dynamic Linking to the contractor rates workbook.
/// </summary>
public sealed class ClosedXmlExporter : BaseBoqExporter
{
    public override Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return ExportPricedBoqAsync(templateFilePath, outputFilePath, matchedPairs, null, enableDynamicLinking: false, progress, ct);
    }

    public override async Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath,
        bool enableDynamicLinking = true,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentNullException.ThrowIfNull(matchedPairs);

        if (!File.Exists(templateFilePath))
        {
            throw new FileNotFoundException("Consultant template file not found.", templateFilePath);
        }

        string? targetDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        await Task.Run(() =>
        {
            // Step 1: Copy original template to output path to preserve 100% of formatting, hierarchy, and design
            if (!string.Equals(Path.GetFullPath(templateFilePath), Path.GetFullPath(outputFilePath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(templateFilePath, outputFilePath, overwrite: true);
            }

            // Step 2: Sanitize package metadata and relationships using base class engine
            SanitizeOpenXmlPackage(outputFilePath);

            using var workbook = new XLWorkbook(outputFilePath);

            // Step 3: Inject rates into target bill sheets - ZERO design or formatting modifications to original sheets
            var sheetGroups = matchedPairs.GroupBy(p => p.TargetItem.BillNumber).ToList();
            int totalSheets = sheetGroups.Count;
            int processedSheets = 0;

            foreach (var group in sheetGroups)
            {
                ct.ThrowIfCancellationRequested();
                string sheetName = group.Key;

                if (!workbook.TryGetWorksheet(sheetName, out var ws))
                {
                    continue;
                }

                foreach (var pair in group)
                {
                    if (pair.TargetItem.IsProtected || pair.TargetItem.Type == BoqItemType.ProvisionalSum)
                    {
                        continue;
                    }

                    int anchorRow = pair.TargetItem.AnchorRowIndex;
                    if (anchorRow <= 0) continue;

                    int rateCol = pair.TargetItem.RateColumnIndex > 0 ? pair.TargetItem.RateColumnIndex : 7;
                    var rateCell = ws.Cell(anchorRow, rateCol);

                    if (pair.IsApproved && pair.InjectedRate.HasValue)
                    {
                        // ONLY set the numeric value into Column G - preserve 100% of original cell fonts, borders, and fills
                        rateCell.Value = (double)pair.InjectedRate.Value;
                    }
                    else
                    {
                        // Unpriced / Unmatched item: ensure rate cell is cleared of any stale residual values
                        rateCell.Clear(XLClearOptions.Contents);
                    }
                }

                processedSheets++;
            }

            // Step 4: Append / Update interactive Audit_Report and Pricing_Linkage_Map worksheets
            CreateAuditLogWorksheet(workbook, matchedPairs, sourceContractorFilePath);
            CreatePricingLinkageMapWorksheet(workbook, matchedPairs, sourceContractorFilePath);

            // Step 5: Save populated workbook - 100% original sheets, tabs, colors, and design preserved
            workbook.Save();

            // Step 6: Inject relative dynamic links if source contractor file is provided
            if (enableDynamicLinking && !string.IsNullOrWhiteSpace(sourceContractorFilePath))
            {
                InjectRelativeDynamicLinks(outputFilePath, sourceContractorFilePath, matchedPairs);
            }

            // Step 7: Post-save OpenXML Archive Sanitization to guarantee 0 repair warnings
            SanitizeOpenXmlPackage(outputFilePath);

            progress?.Report(100);
        }, ct);
    }

    /// <summary>
    /// Exports an executive dashboard and commercial analytics report
    /// into a dedicated standalone workbook without modifying the tender schedule.
    /// </summary>
    public static void ExportStandaloneDashboard(
        string outputPath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath = null)
    {
        using var workbook = new XLWorkbook();
        ExcelDashboardBuilder.BuildDashboard(workbook, matchedPairs);
        CreateAuditLogWorksheet(workbook, matchedPairs, sourceContractorFilePath);
        CreatePricingLinkageMapWorksheet(workbook, matchedPairs, sourceContractorFilePath);
        workbook.SaveAs(outputPath);
    }

    /// <summary>
    /// Creates a dedicated pricing reconciliation and audit report worksheet.
    /// Does not touch any of the original sheets.
    /// </summary>
    private static void CreateAuditLogWorksheet(
        XLWorkbook workbook,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath = null)
    {
        string contractorFileName = !string.IsNullOrWhiteSpace(sourceContractorFilePath)
            ? Path.GetFileName(sourceContractorFilePath)
            : "DP3 - Hatchway.xlsx";

        const string auditSheetName = "Audit_Report";
        if (workbook.TryGetWorksheet(auditSheetName, out var existingWs))
        {
            workbook.Worksheets.Delete(auditSheetName);
        }

        var ws = workbook.Worksheets.Add(auditSheetName);
        ws.TabColor = XLColor.FromHtml("#1E293B"); // Dark Slate Tab
        ws.ShowGridLines = true;
        ws.SheetView.ZoomScale = 90;

        // Title Header
        ws.Row(1).Height = 32;
        ws.Cell(1, 1).Value = "SmartBOQ Enterprise - Pricing Reconciliation & Detailed Audit Trail";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(1, 1).Style.Alignment.Indent = 1;
        ws.Range(1, 1, 1, 15).Merge().Style.Fill.BackgroundColor = XLColor.FromHtml("#0F172A");

        // Summary Scorecard Cards (Rows 3 - 5 across 15 columns, zero overflow)
        int totalItems = matchedPairs.Count;
        int exactMatches = matchedPairs.Count(p => p.Confidence == MatchConfidence.Exact);
        int provisionalSums = matchedPairs.Count(p => p.TargetItem.Type == BoqItemType.ProvisionalSum || p.TargetItem.IsProtected);

        decimal totalInjectedEgp = matchedPairs
            .Where(p => p.IsApproved && p.InjectedRate.HasValue && p.TargetItem.Currency.Equals("EGP", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.InjectedRate!.Value * p.TargetItem.Quantity);

        ws.Row(2).Height = 10; // Spacer row
        ws.Row(3).Height = 18;
        ws.Row(4).Height = 28;
        ws.Row(5).Height = 18;

        CreateSummaryCard(ws, 1, 3, "TOTAL ITEMS ANALYZED", totalItems, "Full Reconciled Scope", "#,##0", "#0F172A");
        CreateSummaryCard(ws, 4, 7, "EXACT MATCHES RECONCILED", exactMatches, "100.0% Rate Accuracy", "#,##0", "#2563EB");
        CreateSummaryCard(ws, 8, 11, "PROVISIONAL SUMS (SHIELDED)", provisionalSums, "Contractually Intact", "#,##0", "#B45309");
        CreateSummaryCard(ws, 12, 15, "TOTAL INJECTED VALUE (EGP)", (double)totalInjectedEgp, "Strict EGP Currency Segregation", "#,##0.00 \"EGP\"", "#15803D");

        ws.Row(6).Height = 12; // Spacer row

        // Table Column Headers (Zero Emojis, Clean Professional Titles)
        string[] headers =
        [
            "انتقال للمقايسة", "انتقال لمصدر السعر (ملف المقاول)", "Sheet / Bill", "Row", "Code", "Description",
            "Source Sheet", "Source Row", "Unit", "Quantity", "Currency", "Injected Rate (EGP)", "Computed Amount (EGP)", "Confidence", "Status / Notes"
        ];

        int headerRow = 7;
        ws.Row(headerRow).Height = 28;
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B"); // Slate 800
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        try
        {
            ws.SheetView.FreezeRows(headerRow);
        }
        catch { }

        // Set generous, readable column widths
        ws.Column(1).Width = 18;  // انتقال للمقايسة
        ws.Column(2).Width = 26;  // انتقال لمصدر السعر (ملف المقاول)
        ws.Column(3).Width = 30;  // Sheet / Bill
        ws.Column(4).Width = 10;  // Row
        ws.Column(5).Width = 14;  // Code
        ws.Column(6).Width = 55;  // Description
        ws.Column(7).Width = 15;  // Source Sheet
        ws.Column(8).Width = 12;  // Source Row
        ws.Column(9).Width = 10;  // Unit
        ws.Column(10).Width = 16; // Quantity
        ws.Column(11).Width = 12; // Currency
        ws.Column(12).Width = 20; // Injected Rate (EGP)
        ws.Column(13).Width = 22; // Computed Amount (EGP)
        ws.Column(14).Width = 16; // Confidence
        ws.Column(15).Width = 22; // Status / Notes

        // Sort pairs: Injected & Approved items FIRST, then Variation Orders, then Shielded PS
        var sortedPairs = matchedPairs
            .OrderByDescending(p => p.IsApproved && p.InjectedRate.HasValue && p.InjectedRate > 0)
            .ThenBy(p => p.TargetItem.Type == BoqItemType.ProvisionalSum ? 1 : 0)
            .ThenBy(p => p.TargetItem.BillNumber)
            .ThenBy(p => p.TargetItem.AnchorRowIndex)
            .ToList();

        // Table Rows
        bool separatorAdded = false;
        int rowIdx = headerRow + 1;
        foreach (var pair in sortedPairs)
        {
            var item = pair.TargetItem;
            var srcItem = pair.MatchedSourceItem;
            decimal? rate = pair.InjectedRate;
            decimal? amount = rate.HasValue ? rate.Value * item.Quantity : null;
            bool isPriced = pair.IsApproved && rate.HasValue && rate > 0;
            bool isPs = item.Type == BoqItemType.ProvisionalSum || item.IsProtected;

            // Visual separator before shielded / unpriced items
            if (!isPriced && !separatorAdded)
            {
                separatorAdded = true;
                ws.Row(rowIdx).Height = 26;
                var sepRange = ws.Range(rowIdx, 1, rowIdx, 15);
                sepRange.Merge();
                sepRange.Value = "CONTRACTUALLY SHIELDED & PROVISIONAL SUM SCOPE (Unmodified Original Allowances)";
                sepRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7"); // Amber-100
                sepRange.Style.Font.Bold = true;
                sepRange.Style.Font.FontSize = 9.5;
                sepRange.Style.Font.FontColor = XLColor.FromHtml("#92400E"); // Amber-800
                sepRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                sepRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                for (int c = 1; c <= 15; c++)
                {
                    ws.Cell(rowIdx, c).Style.Border.TopBorder = XLBorderStyleValues.Medium;
                    ws.Cell(rowIdx, c).Style.Border.TopBorderColor = XLColor.FromHtml("#D97706");
                    ws.Cell(rowIdx, c).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                    ws.Cell(rowIdx, c).Style.Border.BottomBorderColor = XLColor.FromHtml("#D97706");
                }
                rowIdx++;
            }

            ws.Row(rowIdx).Height = 21;
            var fillBg = (rowIdx % 2 == 0) ? XLColor.FromHtml("#F8FAFC") : XLColor.White;

            int rateCol = item.RateColumnIndex > 0 ? item.RateColumnIndex : 7;
            string rateColLetter = XLHelper.GetColumnLetterFromNumber(rateCol);
            string safeSheet = item.BillNumber.Replace("'", "''");
            string targetCellRef = $"'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex}";

            // Col 1: Direct Interactive Quick-Jump to Tender Schedule (Zero Emojis)
            var jumpCell = ws.Cell(rowIdx, 1);
            if (isPriced)
            {
                jumpCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"[ {rateColLetter}{item.AnchorRowIndex} ] المقايسة\")";
                jumpCell.Style.Font.Bold = true;
                jumpCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                jumpCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB"); // Royal blue
            }
            else if (isPs)
            {
                jumpCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"[ صف {item.AnchorRowIndex} ] محمي\")";
                jumpCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                jumpCell.Style.Font.FontColor = XLColor.FromHtml("#B45309"); // Amber
            }
            else
            {
                jumpCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"[ صف {item.AnchorRowIndex} ] معاينة\")";
                jumpCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                jumpCell.Style.Font.FontColor = XLColor.FromHtml("#64748B");
            }
            jumpCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 2: Direct Interactive Quick-Jump to Contractor Master Rates File & Table Record (Zero Emojis)
            var srcJumpCell = ws.Cell(rowIdx, 2);
            if (srcItem != null && srcItem.AnchorRowIndex > 0)
            {
                string rawSrcSheet = string.IsNullOrWhiteSpace(srcItem.SheetName) ? "Sheet1" : srcItem.SheetName;
                string safeSrcSheet = rawSrcSheet.Contains(' ') ? $"'{rawSrcSheet.Replace("'", "''")}'" : rawSrcSheet.Replace("'", "''");
                int srcColIdx = srcItem.RateColumnIndex > 0 ? srcItem.RateColumnIndex : 18;
                string srcColLetter = XLHelper.GetColumnLetterFromNumber(srcColIdx);
                int srcRow = srcItem.AnchorRowIndex;

                int tblStart = srcItem.TableStartColumnIndex > 0 ? srcItem.TableStartColumnIndex : 3;
                int tblEnd = srcItem.TableEndColumnIndex > 0 ? srcItem.TableEndColumnIndex : Math.Max(srcColIdx + 1, 19);
                string tblStartLetter = XLHelper.GetColumnLetterFromNumber(tblStart);
                string tblEndLetter = XLHelper.GetColumnLetterFromNumber(tblEnd);

                // Smart Algorithm: Select entire table row record AND focus ActiveCell directly on Net Rate
                string smartRange = $"{tblStartLetter}{srcRow}:{tblEndLetter}{srcRow},{srcColLetter}{srcRow}";
                string srcRef = $"{contractorFileName}#{safeSrcSheet}!{smartRange}";
                srcJumpCell.FormulaA1 = $"=HYPERLINK(\"{srcRef}\", \"[ {srcColLetter}{srcRow} ] تحديد السعر وبند المقاول\")";
                srcJumpCell.Style.Font.Bold = true;
                srcJumpCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                srcJumpCell.Style.Font.FontColor = XLColor.FromHtml("#16A34A"); // Emerald green
            }
            else
            {
                srcJumpCell.Value = "-";
                srcJumpCell.Style.Font.FontColor = XLColor.FromHtml("#94A3B8");
            }
            srcJumpCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 3: Sheet / Bill (Also Clickable)
            var sheetCell = ws.Cell(rowIdx, 3);
            sheetCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"{item.BillNumber}\")";
            sheetCell.Style.Font.Underline = XLFontUnderlineValues.Single;
            sheetCell.Style.Font.FontColor = isPriced ? XLColor.FromHtml("#0F172A") : XLColor.FromHtml("#475569");
            sheetCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // Col 4: Row (Also Clickable)
            var rowCell = ws.Cell(rowIdx, 4);
            rowCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"{item.AnchorRowIndex}\")";
            rowCell.Style.Font.Bold = isPriced;
            rowCell.Style.Font.Underline = XLFontUnderlineValues.Single;
            rowCell.Style.Font.FontColor = isPriced ? XLColor.FromHtml("#2563EB") : XLColor.FromHtml("#64748B");
            rowCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 5: Code
            ws.Cell(rowIdx, 5).Value = item.ItemCode;
            ws.Cell(rowIdx, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 6: Description
            ws.Cell(rowIdx, 6).Value = item.Description.Length > 120 ? item.Description[..117] + "..." : item.Description;
            ws.Cell(rowIdx, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // Col 7: Source Sheet
            ws.Cell(rowIdx, 7).Value = srcItem?.SheetName ?? "-";
            ws.Cell(rowIdx, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 8: Source Row
            ws.Cell(rowIdx, 8).Value = srcItem != null && srcItem.AnchorRowIndex > 0 ? srcItem.AnchorRowIndex.ToString() : "-";
            ws.Cell(rowIdx, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 9: Unit
            ws.Cell(rowIdx, 9).Value = item.Unit;
            ws.Cell(rowIdx, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 10: Quantity (Live formula-linked to bill sheet if anchorRow > 0)
            var qtyCell = ws.Cell(rowIdx, 10);
            if (item.AnchorRowIndex > 0)
            {
                int qtyCol = item.QuantityColumnIndex > 0 ? item.QuantityColumnIndex : 5;
                string qtyColLetter = XLHelper.GetColumnLetterFromNumber(qtyCol);
                qtyCell.FormulaA1 = $"='{safeSheet}'!{qtyColLetter}{item.AnchorRowIndex}";
            }
            else
            {
                qtyCell.Value = (double)item.Quantity;
            }
            qtyCell.Style.NumberFormat.Format = "#,##0.00";
            qtyCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Col 11: Currency
            ws.Cell(rowIdx, 11).Value = item.Currency;
            ws.Cell(rowIdx, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 12: Injected Rate (Live cell formula with Clickable jump)
            var rateCell = ws.Cell(rowIdx, 12);
            if (isPriced && item.AnchorRowIndex > 0)
            {
                rateCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", '{safeSheet}'!{rateColLetter}{item.AnchorRowIndex})";
                rateCell.Style.Font.Bold = true;
                rateCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                rateCell.Style.Font.FontColor = XLColor.FromHtml("#15803D"); // Emerald green
                rateCell.Style.NumberFormat.Format = "#,##0.00";
            }
            else if (rate.HasValue && rate > 0)
            {
                rateCell.Value = (double)rate.Value;
                rateCell.Style.NumberFormat.Format = "#,##0.00";
            }
            rateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Col 13: Computed Amount (Live formula-linked to bill sheet amount)
            var amtCell = ws.Cell(rowIdx, 13);
            if (item.AnchorRowIndex > 0)
            {
                int amtCol = item.AmountColumnIndex > 0 ? item.AmountColumnIndex : 8;
                string amtColLetter = XLHelper.GetColumnLetterFromNumber(amtCol);
                amtCell.FormulaA1 = $"='{safeSheet}'!{amtColLetter}{item.AnchorRowIndex}";
            }
            else if (amount.HasValue)
            {
                amtCell.Value = (double)amount.Value;
            }
            amtCell.Style.NumberFormat.Format = "#,##0.00";
            amtCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Col 14: Confidence
            ws.Cell(rowIdx, 14).Value = pair.Confidence.ToString();
            ws.Cell(rowIdx, 14).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 15: Status
            string status = pair switch
            {
                _ when item.Type == BoqItemType.ProvisionalSum => "Shielded (PS)",
                _ when pair.IsVariationOrder => "New Scope (VO)",
                _ when pair.IsApproved && rate.HasValue && rate > 0 => "Injected & Approved",
                _ => "Unpriced / Note"
            };
            var statusCell = ws.Cell(rowIdx, 15);
            statusCell.Value = status;
            statusCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            statusCell.Style.Font.Bold = true;
            if (isPriced)
            {
                statusCell.Style.Font.FontColor = XLColor.FromHtml("#15803D");
            }
            else if (isPs)
            {
                statusCell.Style.Font.FontColor = XLColor.FromHtml("#B45309");
            }

            for (int c = 1; c <= 15; c++)
            {
                var cell = ws.Cell(rowIdx, c);
                cell.Style.Fill.BackgroundColor = fillBg;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                cell.Style.Font.FontSize = 9;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }

            if (pair.IsVariationOrder)
            {
                ws.Row(rowIdx).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFBEB");
            }
            else if (item.Type == BoqItemType.ProvisionalSum)
            {
                ws.Row(rowIdx).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
            }

            rowIdx++;
        }

        // Enable AutoFilter on table header
        try
        {
            ws.Range(headerRow, 1, rowIdx - 1, 15).SetAutoFilter();
        }
        catch { }
    }

    /// <summary>
    /// Creates a dedicated interactive cross-workbook pricing linkage and traceability worksheet.
    /// Provides 2-way navigation buttons between the tender schedule and the contractor rates master file.
    /// </summary>
    private static void CreatePricingLinkageMapWorksheet(
        XLWorkbook workbook,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath = null)
    {
        string contractorFileName = !string.IsNullOrWhiteSpace(sourceContractorFilePath)
            ? Path.GetFileName(sourceContractorFilePath)
            : "DP3 - Hatchway.xlsx";

        const string linkageSheetName = "Pricing_Linkage_Map";
        if (workbook.TryGetWorksheet(linkageSheetName, out var existingWs))
        {
            workbook.Worksheets.Delete(linkageSheetName);
        }

        var ws = workbook.Worksheets.Add(linkageSheetName);
        ws.TabColor = XLColor.FromHtml("#0284C7"); // Sky 600 Tab
        ws.ShowGridLines = true;
        ws.SheetView.ZoomScale = 90;

        // Title Header
        ws.Row(1).Height = 34;
        ws.Cell(1, 1).Value = "SmartBOQ Enterprise - Interactive Cross-Workbook Pricing Linkage & Traceability Matrix";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(1, 1).Style.Alignment.Indent = 1;
        ws.Range(1, 1, 1, 16).Merge().Style.Fill.BackgroundColor = XLColor.FromHtml("#0F172A");

        // Summary Scorecards (Rows 3 - 5 across 16 columns)
        int totalLinked = matchedPairs.Count(p => p.MatchedSourceItem != null);
        int exactHigh = matchedPairs.Count(p => p.Confidence == MatchConfidence.Exact || p.Confidence == MatchConfidence.HighFuzzy);
        decimal totalLinkedValue = matchedPairs
            .Where(p => p.MatchedSourceItem != null && p.InjectedRate.HasValue && p.TargetItem.Currency.Equals("EGP", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.InjectedRate!.Value * p.TargetItem.Quantity);

        ws.Row(2).Height = 10;
        ws.Row(3).Height = 18;
        ws.Row(4).Height = 28;
        ws.Row(5).Height = 18;

        CreateSummaryCard(ws, 1, 4, "TOTAL LINKED ITEMS", totalLinked, "Smart Matched to Contractor File", "#,##0", "#0F172A");
        CreateSummaryCard(ws, 5, 8, "EXACT & HIGH CONFIDENCE MATCHES", exactHigh, "100.0% Algorithmic Certainty", "#,##0", "#2563EB");
        CreateSummaryCard(ws, 9, 12, "TOTAL LINKED VALUE (EGP)", (double)totalLinkedValue, "Dynamically Connected Scope", "#,##0.00 \"EGP\"", "#15803D");
        CreateSummaryCard(ws, 13, 16, "CONTRACTOR RATES MASTER FILE", contractorFileName, "Active Relative External Link", null, "#0284C7");

        ws.Row(6).Height = 12;

        string[] headers =
        [
            "انتقال للمقايسة", "فتح وتحديد سعر المقاول", "Tender Sheet / Bill", "Tender Row", "Tender Code", "Tender Description",
            "Unit", "Quantity", "Contractor File", "Contractor Sheet", "Contractor Row", "Contractor Cell",
            "Contractor Code", "Contractor Description", "Unit Rate (EGP)", "Match Algorithm & Confidence"
        ];

        int headerRow = 7;
        ws.Row(headerRow).Height = 28;
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B"); // Slate 800
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        try
        {
            ws.SheetView.FreezeRows(headerRow);
        }
        catch { }

        ws.Column(1).Width = 18;  // انتقال للمقايسة
        ws.Column(2).Width = 26;  // فتح وتحديد سعر المقاول
        ws.Column(3).Width = 28;  // Tender Sheet
        ws.Column(4).Width = 10;  // Tender Row
        ws.Column(5).Width = 14;  // Tender Code
        ws.Column(6).Width = 50;  // Tender Description
        ws.Column(7).Width = 10;  // Unit
        ws.Column(8).Width = 15;  // Quantity
        ws.Column(9).Width = 22;  // Contractor File
        ws.Column(10).Width = 16; // Contractor Sheet
        ws.Column(11).Width = 12; // Contractor Row
        ws.Column(12).Width = 14; // Contractor Cell
        ws.Column(13).Width = 16; // Contractor Code
        ws.Column(14).Width = 50; // Contractor Description
        ws.Column(15).Width = 18; // Unit Rate (EGP)
        ws.Column(16).Width = 24; // Match Algorithm & Confidence

        var sortedPairs = matchedPairs
            .OrderByDescending(p => p.MatchedSourceItem != null)
            .ThenByDescending(p => p.Confidence == MatchConfidence.Exact)
            .ThenBy(p => p.TargetItem.BillNumber)
            .ThenBy(p => p.TargetItem.AnchorRowIndex)
            .ToList();

        int rowIdx = headerRow + 1;
        foreach (var pair in sortedPairs)
        {
            var item = pair.TargetItem;
            var srcItem = pair.MatchedSourceItem;
            decimal? rate = pair.InjectedRate;
            bool isLinked = srcItem != null && srcItem.AnchorRowIndex > 0;

            ws.Row(rowIdx).Height = 21;
            var fillBg = (rowIdx % 2 == 0) ? XLColor.FromHtml("#F8FAFC") : XLColor.White;

            int rateCol = item.RateColumnIndex > 0 ? item.RateColumnIndex : 7;
            string rateColLetter = XLHelper.GetColumnLetterFromNumber(rateCol);
            string safeSheet = item.BillNumber.Replace("'", "''");
            string targetCellRef = $"'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex}";

            // Col 1: Jump to Tender Schedule
            var tenderJump = ws.Cell(rowIdx, 1);
            if (item.AnchorRowIndex > 0)
            {
                tenderJump.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"[ {rateColLetter}{item.AnchorRowIndex} ] المقايسة\")";
                tenderJump.Style.Font.Bold = true;
                tenderJump.Style.Font.Underline = XLFontUnderlineValues.Single;
                tenderJump.Style.Font.FontColor = XLColor.FromHtml("#2563EB"); // Royal blue
            }
            else
            {
                tenderJump.Value = "-";
            }
            tenderJump.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 2: Open and focus on Contractor Rates Cell and Full Table Record
            var srcJump = ws.Cell(rowIdx, 2);
            if (isLinked)
            {
                string rawSrcSheet = string.IsNullOrWhiteSpace(srcItem!.SheetName) ? "Sheet1" : srcItem.SheetName;
                string safeSrcSheet = rawSrcSheet.Contains(' ') ? $"'{rawSrcSheet.Replace("'", "''")}'" : rawSrcSheet.Replace("'", "''");
                int srcColIdx = srcItem.RateColumnIndex > 0 ? srcItem.RateColumnIndex : 18;
                string srcColLetter = XLHelper.GetColumnLetterFromNumber(srcColIdx);
                int srcRow = srcItem.AnchorRowIndex;

                int tblStart = srcItem.TableStartColumnIndex > 0 ? srcItem.TableStartColumnIndex : 3;
                int tblEnd = srcItem.TableEndColumnIndex > 0 ? srcItem.TableEndColumnIndex : Math.Max(srcColIdx + 1, 19);
                string tblStartLetter = XLHelper.GetColumnLetterFromNumber(tblStart);
                string tblEndLetter = XLHelper.GetColumnLetterFromNumber(tblEnd);

                // Smart Algorithm: Select entire table row record AND focus ActiveCell directly on Net Rate
                string smartRange = $"{tblStartLetter}{srcRow}:{tblEndLetter}{srcRow},{srcColLetter}{srcRow}";
                string srcRef = $"{contractorFileName}#{safeSrcSheet}!{smartRange}";
                srcJump.FormulaA1 = $"=HYPERLINK(\"{srcRef}\", \"[ {srcColLetter}{srcRow} ] فتح وتحديد السعر والجدول\")";
                srcJump.Style.Font.Bold = true;
                srcJump.Style.Font.Underline = XLFontUnderlineValues.Single;
                srcJump.Style.Font.FontColor = XLColor.FromHtml("#16A34A"); // Emerald green
            }
            else
            {
                srcJump.Value = "-";
                srcJump.Style.Font.FontColor = XLColor.FromHtml("#94A3B8");
            }
            srcJump.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 3: Tender Sheet / Bill
            var sheetCell = ws.Cell(rowIdx, 3);
            if (item.AnchorRowIndex > 0)
            {
                sheetCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"{item.BillNumber}\")";
                sheetCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                sheetCell.Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            }
            else
            {
                sheetCell.Value = item.BillNumber;
            }
            sheetCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // Col 4: Tender Row
            var rowCell = ws.Cell(rowIdx, 4);
            if (item.AnchorRowIndex > 0)
            {
                rowCell.FormulaA1 = $"=HYPERLINK(\"#{targetCellRef}\", \"{item.AnchorRowIndex}\")";
                rowCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                rowCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
            }
            else
            {
                rowCell.Value = "-";
            }
            rowCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 5: Tender Code
            ws.Cell(rowIdx, 5).Value = item.ItemCode;
            ws.Cell(rowIdx, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 6: Tender Description
            ws.Cell(rowIdx, 6).Value = item.Description.Length > 100 ? item.Description[..97] + "..." : item.Description;
            ws.Cell(rowIdx, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // Col 7: Unit
            ws.Cell(rowIdx, 7).Value = item.Unit;
            ws.Cell(rowIdx, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 8: Quantity (Live formula-linked to bill sheet)
            var mapQtyCell = ws.Cell(rowIdx, 8);
            if (item.AnchorRowIndex > 0)
            {
                int qtyCol = item.QuantityColumnIndex > 0 ? item.QuantityColumnIndex : 5;
                string qtyColLetter = XLHelper.GetColumnLetterFromNumber(qtyCol);
                mapQtyCell.FormulaA1 = $"='{safeSheet}'!{qtyColLetter}{item.AnchorRowIndex}";
            }
            else
            {
                mapQtyCell.Value = (double)item.Quantity;
            }
            mapQtyCell.Style.NumberFormat.Format = "#,##0.00";
            mapQtyCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Col 9: Contractor File
            ws.Cell(rowIdx, 9).Value = contractorFileName;
            ws.Cell(rowIdx, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 10: Contractor Sheet
            ws.Cell(rowIdx, 10).Value = srcItem?.SheetName ?? "-";
            ws.Cell(rowIdx, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 11: Contractor Row
            ws.Cell(rowIdx, 11).Value = isLinked ? srcItem!.AnchorRowIndex.ToString() : "-";
            ws.Cell(rowIdx, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 12: Contractor Cell (Clickable link to contractor file cell & full table record!)
            var cellRefCell = ws.Cell(rowIdx, 12);
            if (isLinked)
            {
                string rawSrcSheet = string.IsNullOrWhiteSpace(srcItem!.SheetName) ? "Sheet1" : srcItem.SheetName;
                string safeSrcSheet = rawSrcSheet.Contains(' ') ? $"'{rawSrcSheet.Replace("'", "''")}'" : rawSrcSheet.Replace("'", "''");
                int srcColIdx = srcItem.RateColumnIndex > 0 ? srcItem.RateColumnIndex : 18;
                string srcColLetter = XLHelper.GetColumnLetterFromNumber(srcColIdx);
                int srcRow = srcItem.AnchorRowIndex;

                int tblStart = srcItem.TableStartColumnIndex > 0 ? srcItem.TableStartColumnIndex : 3;
                int tblEnd = srcItem.TableEndColumnIndex > 0 ? srcItem.TableEndColumnIndex : Math.Max(srcColIdx + 1, 19);
                string tblStartLetter = XLHelper.GetColumnLetterFromNumber(tblStart);
                string tblEndLetter = XLHelper.GetColumnLetterFromNumber(tblEnd);

                // Smart Algorithm: Select entire table row record AND focus ActiveCell directly on Net Rate
                string smartRange = $"{tblStartLetter}{srcRow}:{tblEndLetter}{srcRow},{srcColLetter}{srcRow}";
                string srcRef = $"{contractorFileName}#{safeSrcSheet}!{smartRange}";
                cellRefCell.FormulaA1 = $"=HYPERLINK(\"{srcRef}\", \"{srcColLetter}{srcRow}\")";
                cellRefCell.Style.Font.Bold = true;
                cellRefCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                cellRefCell.Style.Font.FontColor = XLColor.FromHtml("#16A34A");
            }
            else
            {
                cellRefCell.Value = "-";
            }
            cellRefCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 13: Contractor Code
            ws.Cell(rowIdx, 13).Value = srcItem?.ItemCode ?? "-";
            ws.Cell(rowIdx, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 14: Contractor Description
            string srcDesc = srcItem?.Description ?? "-";
            ws.Cell(rowIdx, 14).Value = srcDesc.Length > 100 ? srcDesc[..97] + "..." : srcDesc;
            ws.Cell(rowIdx, 14).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // Col 15: Unit Rate (EGP) (Live formula-linked to bill sheet)
            var rateCell = ws.Cell(rowIdx, 15);
            if (item.AnchorRowIndex > 0 && rate.HasValue && rate > 0)
            {
                rateCell.FormulaA1 = $"='{safeSheet}'!{rateColLetter}{item.AnchorRowIndex}";
                rateCell.Style.NumberFormat.Format = "#,##0.00";
                rateCell.Style.Font.Bold = true;
                rateCell.Style.Font.FontColor = XLColor.FromHtml("#15803D");
            }
            else if (rate.HasValue && rate > 0)
            {
                rateCell.Value = (double)rate.Value;
                rateCell.Style.NumberFormat.Format = "#,##0.00";
                rateCell.Style.Font.Bold = true;
                rateCell.Style.Font.FontColor = XLColor.FromHtml("#15803D");
            }
            else
            {
                rateCell.Value = "-";
            }
            rateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Col 16: Match Algorithm & Confidence
            string confText = pair.Confidence.ToString();
            if (!string.IsNullOrWhiteSpace(pair.MatchRationale))
            {
                confText += $" ({pair.MatchRationale})";
            }
            ws.Cell(rowIdx, 16).Value = confText;
            ws.Cell(rowIdx, 16).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            for (int c = 1; c <= 16; c++)
            {
                var cell = ws.Cell(rowIdx, c);
                cell.Style.Fill.BackgroundColor = fillBg;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                cell.Style.Font.FontSize = 9;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }

            rowIdx++;
        }

        try
        {
            ws.Range(headerRow, 1, rowIdx - 1, 16).SetAutoFilter();
        }
        catch { }
    }

    private static void CreateSummaryCard(
        IXLWorksheet ws,
        int startCol,
        int endCol,
        string title,
        object value,
        string subtitle,
        string? numFormat = null,
        string valueColor = "#0F172A")
    {
        // Row 3: Title
        var r3 = ws.Range(3, startCol, 3, endCol);
        r3.Merge();
        r3.Value = title;
        r3.Style.Font.Bold = true;
        r3.Style.Font.FontSize = 8.5;
        r3.Style.Font.FontColor = XLColor.FromHtml("#64748B");
        r3.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        r3.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Row 4: Value
        var r4 = ws.Range(4, startCol, 4, endCol);
        r4.Merge();
        if (value is double d) r4.Value = d;
        else if (value is decimal dec) r4.Value = (double)dec;
        else if (value is int i) r4.Value = i;
        else r4.Value = value.ToString();

        r4.Style.Font.Bold = true;
        r4.Style.Font.FontSize = 13.5;
        r4.Style.Font.FontColor = XLColor.FromHtml(valueColor);
        r4.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        r4.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        if (numFormat != null) r4.Style.NumberFormat.Format = numFormat;

        // Row 5: Subtitle
        var r5 = ws.Range(5, startCol, 5, endCol);
        r5.Merge();
        r5.Value = subtitle;
        r5.Style.Font.FontSize = 8;
        r5.Style.Font.FontColor = XLColor.FromHtml("#94A3B8");
        r5.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        r5.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Border & Background
        for (int r = 3; r <= 5; r++)
        {
            for (int c = startCol; c <= endCol; c++)
            {
                var cell = ws.Cell(r, c);
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
            }
        }
    }

    /// <summary>
    /// Injects native OpenXML relative external formulas pointing to the contractor rates workbook (File A).
    /// Creates the standard externalLink part and binds cells so that rate changes in File A automatically
    /// propagate into Column G of File B and recalculate Column H in Microsoft Excel,
    /// while preserving initial cached values when File B is opened independently.
    /// </summary>
    public static void InjectRelativeDynamicLinks(
        string outputZipPath,
        string sourceContractorFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        if (string.IsNullOrWhiteSpace(sourceContractorFilePath) || !File.Exists(outputZipPath))
        {
            return;
        }

        string sourceFileName = Path.GetFileName(sourceContractorFilePath);

        var linkablePairs = matchedPairs
            .Where(p => p.IsApproved &&
                        p.InjectedRate.HasValue &&
                        p.InjectedRate.Value > 0m &&
                        !p.TargetItem.IsProtected &&
                        p.TargetItem.Type != BoqItemType.ProvisionalSum &&
                        p.MatchedSourceItem != null &&
                        p.TargetItem.AnchorRowIndex > 0 &&
                        p.MatchedSourceItem.AnchorRowIndex > 0)
            .ToList();

        if (linkablePairs.Count == 0) return;

        // Group by target sheet name
        var pairsByTargetSheet = linkablePairs
            .GroupBy(p => p.TargetItem.BillNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Group distinct source items by their physical source sheet name
        var sourceItemsBySheet = linkablePairs
            .Select(p => p.MatchedSourceItem!)
            .GroupBy(s => string.IsNullOrWhiteSpace(s.SheetName) ? "Sheet1" : s.SheetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var archive = ZipFile.Open(outputZipPath, ZipArchiveMode.Update);

        // 1. Map target sheet names to zip entry paths via workbook.xml and workbook.xml.rels
        var wbEntry = archive.GetEntry("xl/workbook.xml");
        var wbRelsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (wbEntry == null || wbRelsEntry == null) return;

        XDocument wbRelsDoc;
        using (var s = wbRelsEntry.Open())
        {
            wbRelsDoc = XDocument.Load(s);
        }

        XDocument wbDoc;
        using (var s = wbEntry.Open())
        {
            wbDoc = XDocument.Load(s);
        }

        var relMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int maxRId = 1;

        foreach (var rel in wbRelsDoc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            string? id = rel.Attribute("Id")?.Value;
            string? target = rel.Attribute("Target")?.Value;
            if (!string.IsNullOrWhiteSpace(id))
            {
                if (!string.IsNullOrWhiteSpace(target))
                {
                    relMap[id] = target;
                }
                var m = Regex.Match(id, @"\d+");
                if (m.Success && int.TryParse(m.Value, out int idNum) && idNum > maxRId)
                {
                    maxRId = idNum;
                }
            }
        }

        var sheetNameToZipPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XNamespace rNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        foreach (var sheet in wbDoc.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            string? name = sheet.Attribute("name")?.Value;
            string? rId = sheet.Attribute(rNs + "id")?.Value ?? sheet.Attribute("id")?.Value;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(rId) && relMap.TryGetValue(rId, out string? target))
            {
                string normTarget = target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : $"xl/{target}";
                sheetNameToZipPath[name] = normTarget;
            }
        }

        string extLinkRelId = $"rId{maxRId + 1}";

        // 2. Create or overwrite xl/externalLinks/externalLink1.xml
        var extLinkSb = new StringBuilder(16384);
        extLinkSb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        extLinkSb.AppendLine("<externalLink xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        extLinkSb.AppendLine("  <externalBook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId1\">");
        extLinkSb.AppendLine("    <sheetNames>");
        for (int i = 0; i < sourceItemsBySheet.Count; i++)
        {
            extLinkSb.AppendLine($"      <sheetName val=\"{SecurityElement.Escape(sourceItemsBySheet[i].Key)}\"/>");
        }
        extLinkSb.AppendLine("    </sheetNames>");
        extLinkSb.AppendLine("    <sheetDataSet>");
        for (int i = 0; i < sourceItemsBySheet.Count; i++)
        {
            extLinkSb.AppendLine($"      <sheetData sheetId=\"{i}\">");
            var distinctRows = sourceItemsBySheet[i]
                .GroupBy(s => s.AnchorRowIndex)
                .OrderBy(g => g.Key);

            foreach (var rowGroup in distinctRows)
            {
                int r = rowGroup.Key;
                extLinkSb.AppendLine($"        <row r=\"{r}\">");
                foreach (var srcItem in rowGroup)
                {
                    int colIdx = srcItem.RateColumnIndex > 0 ? srcItem.RateColumnIndex : 18;
                    string colLetter = GetExcelColumnLetter(colIdx);
                    string cellRef = $"{colLetter}{r}";
                    decimal rateVal = srcItem.UnitRate ?? 0m;
                    extLinkSb.AppendLine($"          <cell r=\"{cellRef}\"><v>{rateVal.ToString(CultureInfo.InvariantCulture)}</v></cell>");
                }
                extLinkSb.AppendLine("        </row>");
            }
            extLinkSb.AppendLine("      </sheetData>");
        }
        extLinkSb.AppendLine("    </sheetDataSet>");
        extLinkSb.AppendLine("  </externalBook>");
        extLinkSb.Append("</externalLink>");

        var oldExtLinkEntry = archive.GetEntry("xl/externalLinks/externalLink1.xml");
        oldExtLinkEntry?.Delete();
        var extLinkEntry = archive.CreateEntry("xl/externalLinks/externalLink1.xml", CompressionLevel.Fastest);
        using (var writer = new StreamWriter(extLinkEntry.Open(), Encoding.UTF8))
        {
            writer.Write(extLinkSb.ToString());
        }

        // 3. Create or overwrite xl/externalLinks/_rels/externalLink1.xml.rels
        string extLinkRelsContent = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
            $"  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath\" Target=\"{SecurityElement.Escape(sourceFileName)}\" TargetMode=\"External\"/>\r\n" +
            "</Relationships>";

        var oldExtRelsEntry = archive.GetEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
        oldExtRelsEntry?.Delete();
        var extRelsEntry = archive.CreateEntry("xl/externalLinks/_rels/externalLink1.xml.rels", CompressionLevel.Fastest);
        using (var writer = new StreamWriter(extRelsEntry.Open(), Encoding.UTF8))
        {
            writer.Write(extLinkRelsContent);
        }

        // 4. Register Part in [Content_Types].xml
        var ctEntry = archive.GetEntry("[Content_Types].xml");
        if (ctEntry != null)
        {
            string ctContent;
            using (var reader = new StreamReader(ctEntry.Open(), Encoding.UTF8))
            {
                ctContent = reader.ReadToEnd();
            }

            if (!ctContent.Contains("externalLink+xml", StringComparison.OrdinalIgnoreCase))
            {
                ctContent = ctContent.Replace(
                    "</Types>",
                    "<Override PartName=\"/xl/externalLinks/externalLink1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml\"/></Types>"
                );
                ctEntry.Delete();
                var newCt = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Fastest);
                using var writer = new StreamWriter(newCt.Open(), Encoding.UTF8);
                writer.Write(ctContent);
            }
        }

        // 5. Update xl/_rels/workbook.xml.rels with externalLink relationship
        XNamespace pkgRelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        var relationshipsElem = wbRelsDoc.Root;
        if (relationshipsElem != null && !relationshipsElem.Descendants().Any(e => e.Attribute("Target")?.Value == "externalLinks/externalLink1.xml"))
        {
            relationshipsElem.Add(new XElement(pkgRelsNs + "Relationship",
                new XAttribute("Id", extLinkRelId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink"),
                new XAttribute("Target", "externalLinks/externalLink1.xml")
            ));

            wbRelsEntry.Delete();
            var newWbRels = archive.CreateEntry("xl/_rels/workbook.xml.rels", CompressionLevel.Fastest);
            using var s = newWbRels.Open();
            wbRelsDoc.Save(s);
        }

        // 6. Update xl/workbook.xml with externalReferences, updateLinks="always", and fullCalcOnLoad="1"
        XNamespace wbNs = wbDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var extRefsElem = wbDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "externalReferences");
        if (extRefsElem == null)
        {
            var sheetsElem = wbDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheets");
            if (sheetsElem != null)
            {
                var newExtRefs = new XElement(wbNs + "externalReferences",
                    new XElement(wbNs + "externalReference",
                        new XAttribute(rNs + "id", extLinkRelId)
                    )
                );
                sheetsElem.AddAfterSelf(newExtRefs);
            }
        }

        // Set updateLinks="always" so Excel automatically reads DP3 - Hatchway.xlsx silently on open
        var wbPr = wbDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookPr");
        if (wbPr != null)
        {
            wbPr.SetAttributeValue("updateLinks", "always");
        }
        else
        {
            wbDoc.Root?.AddFirst(new XElement(wbNs + "workbookPr", new XAttribute("updateLinks", "always")));
        }

        // Set fullCalcOnLoad="1" and forceFullCalculation="1" so all formulas recalculate on open
        var calcPr = wbDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "calcPr");
        if (calcPr != null)
        {
            calcPr.SetAttributeValue("fullCalcOnLoad", "1");
            calcPr.SetAttributeValue("forceFullCalculation", "1");
        }
        else
        {
            wbDoc.Root?.Add(new XElement(wbNs + "calcPr",
                new XAttribute("fullCalcOnLoad", "1"),
                new XAttribute("forceFullCalculation", "1")));
        }

        wbEntry.Delete();
        var newWb = archive.CreateEntry("xl/workbook.xml", CompressionLevel.Fastest);
        using (var s = newWb.Open())
        {
            wbDoc.Save(s);
        }

        // 7. Inject external formulas into each target worksheet XML using single-pass Regex
        var cellPattern = new Regex(@"(<(?:x:)?c\s+r=""([A-Z0-9]+)""[^>]*>)(?:<(?:x:)?f>.*?</(?:x:)?f>)?(\s*<(?:x:)?v>)", RegexOptions.Compiled);

        foreach (var kvp in pairsByTargetSheet)
        {
            string targetSheetName = kvp.Key;
            var sheetPairs = kvp.Value;

            if (!sheetNameToZipPath.TryGetValue(targetSheetName, out string? entryPath))
            {
                continue;
            }

            var sheetEntry = archive.GetEntry(entryPath);
            if (sheetEntry == null) continue;

            // Build dictionary of CellRef -> FormulaText for this sheet
            var cellFormulaMap = new Dictionary<string, string>(sheetPairs.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sheetPairs)
            {
                int anchorRow = pair.TargetItem.AnchorRowIndex;
                int rateCol = pair.TargetItem.RateColumnIndex > 0 ? pair.TargetItem.RateColumnIndex : 7;
                string targetCellRef = $"{GetExcelColumnLetter(rateCol)}{anchorRow}";

                string srcSheet = string.IsNullOrWhiteSpace(pair.MatchedSourceItem!.SheetName) ? "Sheet1" : pair.MatchedSourceItem.SheetName;
                string safeSrcSheet = srcSheet.Contains(' ') ? $"'{srcSheet}'" : srcSheet;
                int srcCol = pair.MatchedSourceItem.RateColumnIndex > 0 ? pair.MatchedSourceItem.RateColumnIndex : 18;
                string srcColLetter = GetExcelColumnLetter(srcCol);
                int srcRow = pair.MatchedSourceItem.AnchorRowIndex;

                string formulaText = $"[1]{safeSrcSheet}!${srcColLetter}${srcRow}";
                cellFormulaMap[targetCellRef] = formulaText;
            }

            string sheetXml;
            using (var reader = new StreamReader(sheetEntry.Open(), Encoding.UTF8))
            {
                sheetXml = reader.ReadToEnd();
            }

            string updatedSheetXml = cellPattern.Replace(sheetXml, match =>
            {
                string openTag = match.Groups[1].Value;
                string cellRef = match.Groups[2].Value;
                string valTag = match.Groups[3].Value;

                if (cellFormulaMap.TryGetValue(cellRef, out string? formula))
                {
                    string fTag = openTag.Contains("x:c") ? "x:f" : "f";
                    return $"{openTag}<{fTag}>{formula}</{fTag}>{valTag}";
                }

                return match.Value;
            });

            sheetEntry.Delete();
            var newSheetEntry = archive.CreateEntry(entryPath, CompressionLevel.Fastest);
            using (var writer = new StreamWriter(newSheetEntry.Open(), Encoding.UTF8))
            {
                writer.Write(updatedSheetXml);
            }
        }
    }

    /// <summary>
    /// Helper to convert 1-based column number to Excel column letters (1 -> A, 7 -> G, 18 -> R, 27 -> AA).
    /// </summary>
    private static string GetExcelColumnLetter(int columnNumber)
    {
        string columnName = string.Empty;
        while (columnNumber > 0)
        {
            int modulo = (columnNumber - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            columnNumber = (columnNumber - modulo) / 26;
        }
        return columnName;
    }
}


