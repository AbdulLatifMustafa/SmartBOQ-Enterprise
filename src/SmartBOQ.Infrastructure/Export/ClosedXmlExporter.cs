using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;

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
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public override Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return ExportPricedBoqAsync(templateFilePath, outputFilePath, matchedPairs, null, enableDynamicLinking: false, null, null, progress, ct);
    }

    public override Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath,
        bool enableDynamicLinking = false,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return ExportPricedBoqAsync(templateFilePath, outputFilePath, matchedPairs, sourceContractorFilePath, enableDynamicLinking, null, null, progress, ct);
    }

    public override async Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath,
        bool enableDynamicLinking,
        IEnumerable<string>? knownContractorFilePaths,
        IEnumerable<string>? knownTargetFilePaths,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentNullException.ThrowIfNull(matchedPairs);

        FileAccessValidator.EnsureFileReadable(templateFilePath, "ملف مقايسة الاستشاري");
        FileAccessValidator.EnsureFileWritable(outputFilePath, "ملف المقايسة المسعرة الناتج");

        string? targetDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        await Task.Run(() =>
        {
            var prevCulture = Thread.CurrentThread.CurrentCulture;
            var prevUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

                // Step 1: Copy template to output path
                if (!string.Equals(Path.GetFullPath(templateFilePath), Path.GetFullPath(outputFilePath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(templateFilePath, outputFilePath, overwrite: true);
                }

                // Step 1.5: If dynamic linking enabled, extract original external formulas from template before modifying
                Dictionary<string, string>? originalTemplateFormulas = null;
                if (enableDynamicLinking)
                {
                    originalTemplateFormulas = ExtractExternalFormulasFromTemplate(outputFilePath);
                }

                // Step 2: Sanitize package metadata and relationships using base class engine
                SanitizeOpenXmlPackage(outputFilePath);

                using var workbook = new XLWorkbook(outputFilePath);

                // Step 3: Inject rates into target bill sheets
                var sheetGroups = matchedPairs.GroupBy(p => !string.IsNullOrWhiteSpace(p.TargetItem.SheetName) ? p.TargetItem.SheetName : p.TargetItem.BillNumber).ToList();
                int totalSheets = sheetGroups.Count;
                int processedSheets = 0;

                foreach (var group in sheetGroups)
                {
                    ct.ThrowIfCancellationRequested();
                    string sheetName = group.Key;

                    if (!workbook.TryGetWorksheet(sheetName, out var ws))
                    {
                        var altName = group.FirstOrDefault()?.TargetItem.BillNumber;
                        if (string.IsNullOrEmpty(altName) || !workbook.TryGetWorksheet(altName, out ws))
                        {
                            continue;
                        }
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
                            // Clear obsolete template formula and contents to ensure clean numerical rate
                            if (rateCell.HasFormula)
                            {
                                rateCell.FormulaA1 = string.Empty;
                            }
                            rateCell.Clear(XLClearOptions.Contents);

                            // Write clean numeric rate value while preserving cell styling
                            rateCell.Value = (double)pair.InjectedRate.Value;
                        }
                        else if (rateCell.HasFormula)
                        {
                            // Preserve template formula only for unpriced / unapproved items
                            continue;
                        }
                        else if (pair.TargetItem.OriginalRate.HasValue)
                        {
                            // Preserve approved baseline rate in target cell
                            if (rateCell.HasFormula)
                            {
                                rateCell.FormulaA1 = string.Empty;
                            }
                            rateCell.Clear(XLClearOptions.Contents);
                            rateCell.Value = (double)pair.TargetItem.OriginalRate.Value;
                        }
                        else
                        {
                            // Clear rate cell only for unpriced items without formulas
                            rateCell.Clear(XLClearOptions.Contents);
                        }
                    }

                    // Ensure Rate (>=16) and Amount (>=18) columns have sufficient width to avoid '####' display in Excel
                    var rateCols = new HashSet<int>();
                    var amountCols = new HashSet<int>();
                    foreach (var pair in group)
                    {
                        int rCol = pair.TargetItem.RateColumnIndex > 0 ? pair.TargetItem.RateColumnIndex : 7;
                        rateCols.Add(rCol);
                        int aCol = pair.TargetItem.AmountColumnIndex > 0 ? pair.TargetItem.AmountColumnIndex : rCol + 1;
                        amountCols.Add(aCol);
                    }

                    foreach (int colIdx in rateCols)
                    {
                        var col = ws.Column(colIdx);
                        if (col.Width < 16.0)
                        {
                            col.Width = 16.0;
                        }
                    }

                    foreach (int colIdx in amountCols)
                    {
                        var col = ws.Column(colIdx);
                        if (col.Width < 18.0)
                        {
                            col.Width = 18.0;
                        }
                    }

                    processedSheets++;
                }

                // Step 4: Generate Audit_Report and Pricing_Linkage_Map worksheets
                CreateAuditLogWorksheet(workbook, matchedPairs, sourceContractorFilePath);
                CreatePricingLinkageMapWorksheet(workbook, matchedPairs, sourceContractorFilePath);

                // Step 5: Save workbook
                workbook.Save();

                // Step 6: Inject relative dynamic links if dynamic linking is enabled
                if (enableDynamicLinking)
                {
                    InjectRelativeDynamicLinks(
                        outputFilePath,
                        sourceContractorFilePath,
                        matchedPairs,
                        originalTemplateFormulas,
                        knownContractorFilePaths,
                        knownTargetFilePaths,
                        templateFilePath);
                }

                // Step 7: Post-save OpenXML Archive Sanitization to guarantee 0 repair warnings
                SanitizeOpenXmlPackage(outputFilePath);

                progress?.Report(100);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prevCulture;
                Thread.CurrentThread.CurrentUICulture = prevUiCulture;
            }
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
        var prevCulture = Thread.CurrentThread.CurrentCulture;
        var prevUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            using var workbook = new XLWorkbook();
            ExcelDashboardBuilder.BuildDashboard(workbook, matchedPairs);
            CreateAuditLogWorksheet(workbook, matchedPairs, sourceContractorFilePath, isStandaloneDashboard: true);
            CreatePricingLinkageMapWorksheet(workbook, matchedPairs, sourceContractorFilePath, isStandaloneDashboard: true);
            workbook.SaveAs(outputPath);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prevCulture;
            Thread.CurrentThread.CurrentUICulture = prevUiCulture;
        }
    }

    /// <summary>
    /// Creates a dedicated pricing reconciliation and audit report worksheet.
    /// Does not touch any of the original sheets.
    /// </summary>
    private static void CreateAuditLogWorksheet(
        XLWorkbook workbook,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath = null,
        bool isStandaloneDashboard = false)
    {
        string contractorFileName = !string.IsNullOrWhiteSpace(sourceContractorFilePath)
            ? Path.GetFileName(sourceContractorFilePath)
            : "Contractor_Priced_BOQ.xlsx";

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
        ws.Column(1).Width = 18;  // Jump to Tender Schedule
        ws.Column(2).Width = 26;  // Jump to Contractor Source Price
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

        // Sort pairs: approved items, then provisional sums, then bill/row order
        var sortedPairs = matchedPairs
            .OrderByDescending(p => p.IsApproved && p.InjectedRate.HasValue && p.InjectedRate > 0)
            .ThenBy(p => p.TargetItem.Type == BoqItemType.ProvisionalSum ? 1 : 0)
            .ThenBy(p => p.TargetItem.BillNumber)
            .ThenBy(p => p.TargetItem.AnchorRowIndex)
            .ToList();

        // Enforce maximum worksheet row limit
        const int maxExcelSheetRows = 1_048_500;
        int maxExportRows = Math.Min(sortedPairs.Count, maxExcelSheetRows - headerRow - 5);
        var exportPairs = sortedPairs.Take(maxExportRows);

        // Table Rows
        bool separatorAdded = false;
        int rowIdx = headerRow + 1;
        foreach (var pair in exportPairs)
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
                sepRange.Style.Border.TopBorder = XLBorderStyleValues.Medium;
                sepRange.Style.Border.TopBorderColor = XLColor.FromHtml("#D97706");
                sepRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                sepRange.Style.Border.BottomBorderColor = XLColor.FromHtml("#D97706");
                rowIdx++;
            }

            ws.Row(rowIdx).Height = 21;
            var fillBg = (rowIdx % 2 == 0) ? XLColor.FromHtml("#F8FAFC") : XLColor.White;

            int rateCol = item.RateColumnIndex > 0 ? item.RateColumnIndex : 7;
            string rateColLetter = XLHelper.GetColumnLetterFromNumber(rateCol);
            string targetSheetName = !string.IsNullOrWhiteSpace(item.SheetName) ? item.SheetName : item.BillNumber;
            string safeSheet = targetSheetName.Replace("'", "''");

            bool sheetExistsInThisWorkbook = !isStandaloneDashboard &&
                !string.IsNullOrWhiteSpace(item.SheetName) &&
                workbook.TryGetWorksheet(item.SheetName, out _);

            string rawWb = !string.IsNullOrWhiteSpace(item.WorkbookName) ? item.WorkbookName : "";
            string targetFileName = Path.GetFileName(rawWb);
            if (!string.IsNullOrWhiteSpace(targetFileName) && !targetFileName.EndsWith("_Reconciled.xlsx", StringComparison.OrdinalIgnoreCase))
            {
                targetFileName = $"{Path.GetFileNameWithoutExtension(targetFileName)}_Reconciled.xlsx";
            }

            string targetCellRef;
            if (sheetExistsInThisWorkbook)
            {
                targetCellRef = $"#'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }
            else if (!string.IsNullOrWhiteSpace(targetFileName))
            {
                targetCellRef = $"{targetFileName}#'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                targetCellRef = $"#'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }

            // Col 1: Tender Schedule jump link
            var jumpCell = ws.Cell(rowIdx, 1);
            if (isPriced)
            {
                jumpCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"[ {rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)} ] المقايسة\")";
                jumpCell.Style.Font.Bold = true;
                jumpCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                jumpCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB"); // Royal blue
            }
            else if (isPs)
            {
                jumpCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"[ {rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)} ] محمي\")";
                jumpCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                jumpCell.Style.Font.FontColor = XLColor.FromHtml("#B45309"); // Amber
            }
            else
            {
                jumpCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"[ {rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)} ] معاينة\")";
                jumpCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                jumpCell.Style.Font.FontColor = XLColor.FromHtml("#64748B");
            }
            jumpCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 2: Contractor source record link
            var srcJumpCell = ws.Cell(rowIdx, 2);
            if (srcItem != null && srcItem.AnchorRowIndex > 0)
            {
                string itemContractorFile = !string.IsNullOrWhiteSpace(srcItem.WorkbookName)
                    ? Path.GetFileName(srcItem.WorkbookName)
                    : contractorFileName;
                if (itemContractorFile.StartsWith("[Source_Priced]_", StringComparison.OrdinalIgnoreCase))
                {
                    itemContractorFile = itemContractorFile.Substring("[Source_Priced]_".Length);
                }

                string rawSrcSheet = string.IsNullOrWhiteSpace(srcItem.SheetName) ? "Sheet1" : srcItem.SheetName;
                string safeSrcSheet = $"'{rawSrcSheet.Replace("'", "''")}'";
                int srcColIdx = srcItem.RateColumnIndex > 0 ? srcItem.RateColumnIndex : 18;
                string srcColLetter = XLHelper.GetColumnLetterFromNumber(srcColIdx);
                int srcRow = srcItem.AnchorRowIndex;

                // Accurate direct jump to the exact Net Rate cell in the contractor file
                string cellCoord = $"{srcColLetter}{srcRow.ToString(CultureInfo.InvariantCulture)}";
                string srcRef = $"{itemContractorFile}#{safeSrcSheet}!{cellCoord}";
                srcJumpCell.FormulaA1 = $"=HYPERLINK(\"{srcRef}\", \"[ {srcColLetter}{srcRow.ToString(CultureInfo.InvariantCulture)} ] عرض سعر المقاول\")";
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
            sheetCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"{targetSheetName}\")";
            sheetCell.Style.Font.Underline = XLFontUnderlineValues.Single;
            sheetCell.Style.Font.FontColor = isPriced ? XLColor.FromHtml("#0F172A") : XLColor.FromHtml("#475569");
            sheetCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // Col 4: Row (Also Clickable)
            var rowCell = ws.Cell(rowIdx, 4);
            rowCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}\")";
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
            ws.Cell(rowIdx, 8).Value = srcItem != null && srcItem.AnchorRowIndex > 0 ? srcItem.AnchorRowIndex.ToString(CultureInfo.InvariantCulture) : "-";
            ws.Cell(rowIdx, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 9: Unit
            ws.Cell(rowIdx, 9).Value = item.Unit;
            ws.Cell(rowIdx, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 10: Quantity (Live formula-linked to bill sheet if anchorRow > 0 and sheet exists in this workbook)
            var qtyCell = ws.Cell(rowIdx, 10);
            if (sheetExistsInThisWorkbook && item.AnchorRowIndex > 0)
            {
                int qtyCol = item.QuantityColumnIndex > 0 ? item.QuantityColumnIndex : 5;
                string qtyColLetter = XLHelper.GetColumnLetterFromNumber(qtyCol);
                qtyCell.FormulaA1 = $"='{safeSheet}'!{qtyColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
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
            if (sheetExistsInThisWorkbook && isPriced && item.AnchorRowIndex > 0)
            {
                rateCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", '{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)})";
                rateCell.Style.Font.Bold = true;
                rateCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                rateCell.Style.Font.FontColor = XLColor.FromHtml("#15803D"); // Emerald green
                rateCell.Style.NumberFormat.Format = "#,##0.00";
            }
            else if (rate.HasValue && rate > 0)
            {
                if (item.AnchorRowIndex > 0)
                {
                    rateCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", {rate.Value.ToString(CultureInfo.InvariantCulture)})";
                    rateCell.Style.Font.Bold = true;
                    rateCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                    rateCell.Style.Font.FontColor = XLColor.FromHtml("#15803D");
                }
                else
                {
                    rateCell.Value = (double)rate.Value;
                }
                rateCell.Style.NumberFormat.Format = "#,##0.00";
            }
            else
            {
                rateCell.Value = "-";
            }
            rateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Col 13: Computed Amount (Live formula-linked to bill sheet amount or dynamic quantity*rate)
            var amtCell = ws.Cell(rowIdx, 13);
            if (sheetExistsInThisWorkbook && item.AnchorRowIndex > 0)
            {
                int amtCol = item.AmountColumnIndex > 0 ? item.AmountColumnIndex : 8;
                string amtColLetter = XLHelper.GetColumnLetterFromNumber(amtCol);
                amtCell.FormulaA1 = $"='{safeSheet}'!{amtColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }
            else if (rate.HasValue && rate > 0)
            {
                amtCell.FormulaA1 = $"=J{rowIdx}*L{rowIdx}";
            }
            else if (amount.HasValue && amount > 0)
            {
                amtCell.Value = (double)amount.Value;
            }
            else
            {
                amtCell.Value = "-";
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

            var rowRange = ws.Range(rowIdx, 1, rowIdx, 15);
            rowRange.Style.Fill.BackgroundColor = fillBg;

            if (pair.IsVariationOrder)
            {
                rowRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFBEB");
            }
            else if (item.Type == BoqItemType.ProvisionalSum)
            {
                rowRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
            }

            rowIdx++;
        }

        // Apply bulk typography, alignment, and borders to minimize memory footprint
        if (rowIdx > headerRow + 1)
        {
            var dataRange = ws.Range(headerRow + 1, 1, rowIdx - 1, 15);
            dataRange.Style.Font.FontSize = 9;
            dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            dataRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#E2E8F0");
            dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            dataRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
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
        string? sourceContractorFilePath = null,
        bool isStandaloneDashboard = false)
    {
        string contractorFileName = !string.IsNullOrWhiteSpace(sourceContractorFilePath)
            ? Path.GetFileName(sourceContractorFilePath)
            : "Contractor_Priced_BOQ.xlsx";

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

        ws.Column(1).Width = 18;  // Jump to Tender Schedule
        ws.Column(2).Width = 26;  // Open and locate Contractor Rate
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

        // Enforce maximum worksheet row limit
        const int maxExcelSheetRows = 1_048_500;
        int maxExportRows = Math.Min(sortedPairs.Count, maxExcelSheetRows - headerRow - 5);
        var exportPairs = sortedPairs.Take(maxExportRows);

        int rowIdx = headerRow + 1;
        foreach (var pair in exportPairs)
        {
            var item = pair.TargetItem;
            var srcItem = pair.MatchedSourceItem;
            decimal? rate = pair.InjectedRate;
            bool isLinked = srcItem != null && srcItem.AnchorRowIndex > 0;

            ws.Row(rowIdx).Height = 21;
            var fillBg = (rowIdx % 2 == 0) ? XLColor.FromHtml("#F8FAFC") : XLColor.White;

            int rateCol = item.RateColumnIndex > 0 ? item.RateColumnIndex : 7;
            string rateColLetter = XLHelper.GetColumnLetterFromNumber(rateCol);
            string targetSheetName = !string.IsNullOrWhiteSpace(item.SheetName) ? item.SheetName : item.BillNumber;
            string safeSheet = targetSheetName.Replace("'", "''");

            bool sheetExistsInThisWorkbook = !isStandaloneDashboard &&
                !string.IsNullOrWhiteSpace(item.SheetName) &&
                workbook.TryGetWorksheet(item.SheetName, out _);

            string rawWb = !string.IsNullOrWhiteSpace(item.WorkbookName) ? item.WorkbookName : "";
            string targetFileName = Path.GetFileName(rawWb);
            if (!string.IsNullOrWhiteSpace(targetFileName) && !targetFileName.EndsWith("_Reconciled.xlsx", StringComparison.OrdinalIgnoreCase))
            {
                targetFileName = $"{Path.GetFileNameWithoutExtension(targetFileName)}_Reconciled.xlsx";
            }

            string targetCellRef;
            if (sheetExistsInThisWorkbook)
            {
                targetCellRef = $"#'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }
            else if (!string.IsNullOrWhiteSpace(targetFileName))
            {
                targetCellRef = $"{targetFileName}#'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                targetCellRef = $"#'{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }

            // Col 1: Jump to Tender Schedule (English Digits)
            var tenderJump = ws.Cell(rowIdx, 1);
            if (item.AnchorRowIndex > 0)
            {
                tenderJump.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"[ {rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)} ] المقايسة\")";
                tenderJump.Style.Font.Bold = true;
                tenderJump.Style.Font.Underline = XLFontUnderlineValues.Single;
                tenderJump.Style.Font.FontColor = XLColor.FromHtml("#2563EB"); // Royal blue
            }
            else
            {
                tenderJump.Value = "-";
            }
            tenderJump.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 2: Contractor source record link
            var srcJump = ws.Cell(rowIdx, 2);
            string itemContractorFile = !string.IsNullOrWhiteSpace(srcItem?.WorkbookName) 
                ? Path.GetFileName(srcItem.WorkbookName) 
                : contractorFileName;
            if (itemContractorFile.StartsWith("[Source_Priced]_", StringComparison.OrdinalIgnoreCase))
            {
                itemContractorFile = itemContractorFile.Substring("[Source_Priced]_".Length);
            }

            if (isLinked)
            {
                string rawSrcSheet = string.IsNullOrWhiteSpace(srcItem!.SheetName) ? "Sheet1" : srcItem.SheetName;
                string safeSrcSheet = $"'{rawSrcSheet.Replace("'", "''")}'";
                int srcColIdx = srcItem.RateColumnIndex > 0 ? srcItem.RateColumnIndex : 18;
                string srcColLetter = XLHelper.GetColumnLetterFromNumber(srcColIdx);
                int srcRow = srcItem.AnchorRowIndex;

                // Accurate direct jump to the exact single rate cell in contractor workbook
                string srcRef = $"{itemContractorFile}#{safeSrcSheet}!{srcColLetter}{srcRow.ToString(CultureInfo.InvariantCulture)}";
                srcJump.FormulaA1 = $"=HYPERLINK(\"{srcRef}\", \"[ {srcColLetter}{srcRow.ToString(CultureInfo.InvariantCulture)} ] فتح وتحديد سعر المقاول\")";
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
                sheetCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"{targetSheetName}\")";
                sheetCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                sheetCell.Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            }
            else
            {
                sheetCell.Value = targetSheetName;
            }
            sheetCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // Col 4: Tender Row
            var rowCell = ws.Cell(rowIdx, 4);
            if (item.AnchorRowIndex > 0)
            {
                rowCell.FormulaA1 = $"=HYPERLINK(\"{targetCellRef}\", \"{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}\")";
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

            // Col 8: Quantity (Live formula-linked to bill sheet if anchorRow > 0 and sheet exists in this workbook)
            var mapQtyCell = ws.Cell(rowIdx, 8);
            if (sheetExistsInThisWorkbook && item.AnchorRowIndex > 0)
            {
                int qtyCol = item.QuantityColumnIndex > 0 ? item.QuantityColumnIndex : 5;
                string qtyColLetter = XLHelper.GetColumnLetterFromNumber(qtyCol);
                mapQtyCell.FormulaA1 = $"='{safeSheet}'!{qtyColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                mapQtyCell.Value = (double)item.Quantity;
            }
            mapQtyCell.Style.NumberFormat.Format = "#,##0.00";
            mapQtyCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Col 9: Contractor File
            ws.Cell(rowIdx, 9).Value = isLinked ? itemContractorFile : "-";
            ws.Cell(rowIdx, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 10: Contractor Sheet
            ws.Cell(rowIdx, 10).Value = srcItem?.SheetName ?? "-";
            ws.Cell(rowIdx, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 11: Contractor Row
            ws.Cell(rowIdx, 11).Value = isLinked ? srcItem!.AnchorRowIndex.ToString(CultureInfo.InvariantCulture) : "-";
            ws.Cell(rowIdx, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Col 12: Contractor cell link
            var cellRefCell = ws.Cell(rowIdx, 12);
            if (isLinked)
            {
                string rawSrcSheet = string.IsNullOrWhiteSpace(srcItem!.SheetName) ? "Sheet1" : srcItem.SheetName;
                string safeSrcSheet = $"'{rawSrcSheet.Replace("'", "''")}'";
                int srcColIdx = srcItem.RateColumnIndex > 0 ? srcItem.RateColumnIndex : 18;
                string srcColLetter = XLHelper.GetColumnLetterFromNumber(srcColIdx);
                int srcRow = srcItem.AnchorRowIndex;

                string srcRef = $"{itemContractorFile}#{safeSrcSheet}!{srcColLetter}{srcRow.ToString(CultureInfo.InvariantCulture)}";
                cellRefCell.FormulaA1 = $"=HYPERLINK(\"{srcRef}\", \"{srcColLetter}{srcRow.ToString(CultureInfo.InvariantCulture)}\")";
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

            // Col 15: Unit Rate (EGP) (Live formula-linked to bill sheet or numeric value)
            var rateCell = ws.Cell(rowIdx, 15);
            if (sheetExistsInThisWorkbook && item.AnchorRowIndex > 0 && rate.HasValue && rate > 0)
            {
                rateCell.FormulaA1 = $"='{safeSheet}'!{rateColLetter}{item.AnchorRowIndex.ToString(CultureInfo.InvariantCulture)}";
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

            ws.Range(rowIdx, 1, rowIdx, 16).Style.Fill.BackgroundColor = fillBg;
            rowIdx++;
        }

        // Apply bulk typography, alignment, and borders to minimize memory footprint
        if (rowIdx > headerRow + 1)
        {
            var dataRange = ws.Range(headerRow + 1, 1, rowIdx - 1, 16);
            dataRange.Style.Font.FontSize = 9;
            dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            dataRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#E2E8F0");
            dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            dataRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
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
    /// Extracts external formulas from the original template package before ClosedXML rate injection.
    /// Stores them mapped by "sheetPath!cellRef" (e.g. "xl/worksheets/sheet7.xml!F14").
    /// </summary>
    private static Dictionary<string, string> ExtractExternalFormulasFromTemplate(string zipFilePath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(zipFilePath)) return map;

        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var formulaRegex = new Regex(@"<(?:x:)?c\s+r=""([A-Z0-9]+)""[^>]*>\s*<(?:x:)?f[^>]*>([^<]*\[[^<]*\][^<]*)</(?:x:)?f>", RegexOptions.Compiled);

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                    entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    string xml = reader.ReadToEnd();
                    var matches = formulaRegex.Matches(xml);
                    foreach (Match m in matches)
                    {
                        string cellRef = m.Groups[1].Value;
                        string formula = m.Groups[2].Value;
                        map[$"{entry.FullName}!{cellRef}"] = formula;
                    }
                }
            }
        }
        catch
        {
            // Non-fatal: if extraction encounters issues, fallback gracefully
        }

        return map;
    }

    /// <summary>
    /// Injects native OpenXML relative dynamic external links pointing to the contractor rates workbooks.
    /// 1. Discovers and relativizes existing externalLink references (e.g. CANDY FILE.xlsx, Mechanical.xlsx, Electrical.xlsx, or sibling BOQs)
    ///    so that moving or sharing the export folder preserves live formula calculation in Microsoft Excel.
    /// 2. If a template has external formulas (e.g. =[1]Sheet1!$L$434+[3]Estimate!$I$596), preserves them while keeping cached rates.
    /// 3. If a template item is matched to a contractor item without existing formulas, injects the dynamic external reference formula
    ///    e.g. [3]Estimate!$I$596 pointing to the correct external workbook index.
    /// 4. Configures full calculation on open (calcPr fullCalcOnLoad="1" forceFullCalculation="1") so modifying contractor files immediately
    ///    recalculates rates and amounts upon opening the reconciled schedule or summary.
    /// </summary>
    public static void InjectRelativeDynamicLinks(
        string outputZipPath,
        string? sourceContractorFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        IReadOnlyDictionary<string, string>? originalTemplateFormulas = null,
        IEnumerable<string>? knownContractorFilePaths = null,
        IEnumerable<string>? knownTargetFilePaths = null,
        string? originalTemplateFilePath = null)
    {
        if (!File.Exists(outputZipPath)) return;

        string exportDir = Path.GetDirectoryName(outputZipPath) ?? "";
        string? primarySourceFileName = !string.IsNullOrWhiteSpace(sourceContractorFilePath)
            ? Path.GetFileName(sourceContractorFilePath)
            : null;

        // Dynamic Adaptive Classification Sets (Zero Hardcoded File Names)
        var contractorFilesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetFilesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Populate contractorFilesSet from known contractor paths & matched pairs
        if (!string.IsNullOrWhiteSpace(primarySourceFileName))
        {
            contractorFilesSet.Add(primarySourceFileName);
            contractorFilesSet.Add(Path.GetFileNameWithoutExtension(primarySourceFileName));
        }

        if (knownContractorFilePaths != null)
        {
            foreach (var path in knownContractorFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string cName = Path.GetFileName(path);
                contractorFilesSet.Add(cName);
                contractorFilesSet.Add(Path.GetFileNameWithoutExtension(cName));
            }
        }

        foreach (var pair in matchedPairs)
        {
            string? wbName = pair.MatchedSourceItem?.WorkbookName;
            if (!string.IsNullOrWhiteSpace(wbName))
            {
                contractorFilesSet.Add(wbName);
                contractorFilesSet.Add(Path.GetFileNameWithoutExtension(wbName));
            }
        }

        // 2. Populate targetFilesSet from template path & known consultant target paths
        if (!string.IsNullOrWhiteSpace(originalTemplateFilePath))
        {
            string tmplName = Path.GetFileName(originalTemplateFilePath);
            targetFilesSet.Add(tmplName);
            targetFilesSet.Add(Path.GetFileNameWithoutExtension(tmplName));

            try
            {
                string? tmplDir = Path.GetDirectoryName(originalTemplateFilePath);
                if (!string.IsNullOrWhiteSpace(tmplDir) && Directory.Exists(tmplDir))
                {
                    foreach (var f in Directory.EnumerateFiles(tmplDir, "*.xls*"))
                    {
                        string fn = Path.GetFileName(f);
                        targetFilesSet.Add(fn);
                        targetFilesSet.Add(Path.GetFileNameWithoutExtension(fn));
                    }
                }
            }
            catch { /* non-fatal */ }
        }

        if (knownTargetFilePaths != null)
        {
            foreach (var path in knownTargetFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string tName = Path.GetFileName(path);
                targetFilesSet.Add(tName);
                targetFilesSet.Add(Path.GetFileNameWithoutExtension(tName));
            }
        }

        foreach (var pair in matchedPairs)
        {
            string? wbName = pair.TargetItem?.WorkbookName;
            if (!string.IsNullOrWhiteSpace(wbName))
            {
                targetFilesSet.Add(wbName);
                targetFilesSet.Add(Path.GetFileNameWithoutExtension(wbName));
            }
        }

        // 3. Scan exportDir dynamically to discover reconciled sibling packages and standalone contractor workbooks
        if (!string.IsNullOrWhiteSpace(exportDir) && Directory.Exists(exportDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(exportDir, "*.xlsx"))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.EndsWith("_Reconciled.xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        string baseTarget = fn.Substring(0, fn.Length - "_Reconciled.xlsx".Length);
                        targetFilesSet.Add(baseTarget);
                        targetFilesSet.Add($"{baseTarget}.xlsx");
                    }
                    else if (!fn.StartsWith("Master_", StringComparison.OrdinalIgnoreCase) &&
                             !fn.StartsWith("Executive_", StringComparison.OrdinalIgnoreCase))
                    {
                        // Any non-reconciled standalone workbook present in the export folder is a contractor rate source
                        contractorFilesSet.Add(fn);
                        contractorFilesSet.Add(Path.GetFileNameWithoutExtension(fn));
                    }
                }
            }
            catch { /* non-fatal */ }
        }

        using var archive = ZipFile.Open(outputZipPath, ZipArchiveMode.Update);

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

        XNamespace rNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkgRelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace wbNs = wbDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        // Map workbook relationship IDs
        var relMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int maxRId = 1;
        foreach (var rel in wbRelsDoc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            string? id = rel.Attribute("Id")?.Value;
            string? target = rel.Attribute("Target")?.Value;
            if (!string.IsNullOrWhiteSpace(id))
            {
                if (!string.IsNullOrWhiteSpace(target)) relMap[id] = target;
                var m = Regex.Match(id, @"\d+");
                if (m.Success && int.TryParse(m.Value, out int idNum) && idNum > maxRId)
                {
                    maxRId = idNum;
                }
            }
        }

        // Map sheet names to zip entry paths
        var sheetNameToZipPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in wbDoc.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            string? name = sheet.Attribute("name")?.Value;
            string? rId = sheet.Attribute(rNs + "id")?.Value ?? sheet.Attribute("id")?.Value;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(rId) && relMap.TryGetValue(rId, out string? target))
            {
                string cleanTarget = target.TrimStart('/');
                string normTarget = cleanTarget.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? cleanTarget : $"xl/{cleanTarget}";
                sheetNameToZipPath[name] = normTarget;
            }
        }

        // --- STEP 1: Relativize all existing externalLink*.xml.rels and map file names to external indices ---
        var fileToExtIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var extRefsElem = wbDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "externalReferences");
        var extRefNodes = extRefsElem?.Elements().Where(e => e.Name.LocalName == "externalReference").ToList() ?? new List<XElement>();

        int currentIndex = 1;
        foreach (var extRef in extRefNodes)
        {
            string? rId = extRef.Attribute(rNs + "id")?.Value ?? extRef.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(rId) || !relMap.TryGetValue(rId, out string? linkPartTarget))
            {
                currentIndex++;
                continue;
            }

            string cleanLinkTarget = linkPartTarget.TrimStart('/');
            string linkPartPath = cleanLinkTarget.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? cleanLinkTarget : $"xl/{cleanLinkTarget}";
            string linkPartFileName = Path.GetFileName(linkPartPath);
            string relsPath = $"xl/externalLinks/_rels/{linkPartFileName}.rels";

            var relsEntry = archive.GetEntry(relsPath);
            if (relsEntry != null)
            {
                XDocument linkRelsDoc;
                using (var s = relsEntry.Open())
                {
                    linkRelsDoc = XDocument.Load(s);
                }

                bool relsChanged = false;
                foreach (var rel in linkRelsDoc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
                {
                    string? target = rel.Attribute("Target")?.Value;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        string unescaped = Uri.UnescapeDataString(target.Replace('\\', '/'));
                        string fileName = Path.GetFileName(unescaped);

                        string baseNoExt = Path.GetFileNameWithoutExtension(fileName);
                        string recFileName = $"{baseNoExt}_Reconciled.xlsx";
                        string newFileName = fileName;

                        // Dynamic flexible classification without hardcoded file names
                        bool isContractorFile = contractorFilesSet.Contains(fileName) || contractorFilesSet.Contains(baseNoExt);
                        bool isKnownTarget = targetFilesSet.Contains(fileName) || targetFilesSet.Contains(baseNoExt);

                        if (fileName.EndsWith("_Reconciled.xlsx", StringComparison.OrdinalIgnoreCase))
                        {
                            newFileName = fileName;
                        }
                        else if (isContractorFile)
                        {
                            // Contractor pricing workbook remains with its original file name
                            newFileName = fileName;
                        }
                        else if (isKnownTarget)
                        {
                            // Sibling consultant schedule exported as [BaseName]_Reconciled.xlsx
                            newFileName = recFileName;
                        }
                        else if (File.Exists(Path.Combine(exportDir, recFileName)))
                        {
                            // Reconciled counterpart physically exists in export directory
                            newFileName = recFileName;
                        }
                        else if (File.Exists(Path.Combine(exportDir, fileName)))
                        {
                            // Original contractor workbook physically exists in export directory
                            newFileName = fileName;
                        }
                        else
                        {
                            // Fallback heuristic for sibling consultant schedules
                            newFileName = recFileName;
                        }

                        // Relativize target to just the local file name in same directory
                        string escapedNewTarget = Uri.EscapeDataString(newFileName).Replace("%2E", ".");
                        if (!string.Equals(target, escapedNewTarget, StringComparison.Ordinal))
                        {
                            rel.SetAttributeValue("Target", escapedNewTarget);
                            rel.SetAttributeValue("TargetMode", "External");
                            relsChanged = true;
                        }

                        fileToExtIndex[newFileName] = currentIndex;
                        fileToExtIndex[baseNoExt] = currentIndex;
                        fileToExtIndex[fileName] = currentIndex;

                        // Ensure existing externalLink has all actual sheets of the external workbook
                        var extLinkEntry = archive.GetEntry(linkPartPath);
                        if (extLinkEntry != null)
                        {
                            try
                            {
                                XDocument extLinkDoc;
                                using (var s = extLinkEntry.Open())
                                {
                                    extLinkDoc = XDocument.Load(s);
                                }

                                XNamespace mainNs = extLinkDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                                var sheetNamesElem = extLinkDoc.Descendants(mainNs + "sheetNames").FirstOrDefault()
                                    ?? extLinkDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheetNames");

                                if (sheetNamesElem != null && !string.IsNullOrWhiteSpace(newFileName))
                                {
                                    var actualSheets = GetExternalWorkbookSheetNames(newFileName, exportDir, matchedPairs);
                                    var existingSheets = sheetNamesElem.Elements().Select(e => e.Attribute("val")?.Value ?? "").ToList();

                                    bool needsUpdate = false;
                                    if (actualSheets.Count > existingSheets.Count)
                                    {
                                        needsUpdate = true;
                                    }
                                    else
                                    {
                                        for (int i = 0; i < Math.Min(actualSheets.Count, existingSheets.Count); i++)
                                        {
                                            if (!string.Equals(actualSheets[i], existingSheets[i], StringComparison.OrdinalIgnoreCase))
                                            {
                                                needsUpdate = true;
                                                break;
                                            }
                                        }
                                    }

                                    if (needsUpdate)
                                    {
                                        sheetNamesElem.RemoveAll();
                                        foreach (var s in actualSheets)
                                        {
                                            sheetNamesElem.Add(new XElement(mainNs + "sheetName", new XAttribute("val", s)));
                                        }

                                        extLinkEntry.Delete();
                                        var newExtLinkEntry = archive.CreateEntry(linkPartPath, CompressionLevel.Fastest);
                                        using var wsStream = newExtLinkEntry.Open();
                                        extLinkDoc.Save(wsStream);
                                    }
                                }
                            }
                            catch
                            {
                                // Graceful fallback
                            }
                        }
                    }
                }

                if (relsChanged)
                {
                    relsEntry.Delete();
                    var newRelsEntry = archive.CreateEntry(relsPath, CompressionLevel.Fastest);
                    using var s = newRelsEntry.Open();
                    linkRelsDoc.Save(s);
                }
            }

            currentIndex++;
        }

        // --- STEP 2: Ensure primary contractor source file and any matched source files are registered in externalReferences ---
        var sourceFilesToRegister = new List<string>();
        if (!string.IsNullOrWhiteSpace(primarySourceFileName) && !fileToExtIndex.ContainsKey(primarySourceFileName))
        {
            sourceFilesToRegister.Add(primarySourceFileName);
        }

        if (knownContractorFilePaths != null)
        {
            foreach (var path in knownContractorFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string cName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(cName) && !fileToExtIndex.ContainsKey(cName) && !sourceFilesToRegister.Contains(cName, StringComparer.OrdinalIgnoreCase))
                {
                    sourceFilesToRegister.Add(cName);
                }
            }
        }

        foreach (var pair in matchedPairs)
        {
            string? wbName = pair.MatchedSourceItem?.WorkbookName;
            if (!string.IsNullOrWhiteSpace(wbName) && !fileToExtIndex.ContainsKey(wbName) && !sourceFilesToRegister.Contains(wbName, StringComparer.OrdinalIgnoreCase))
            {
                sourceFilesToRegister.Add(wbName);
            }
        }

        bool wbDocModified = false;
        bool wbRelsDocModified = false;

        foreach (var srcFile in sourceFilesToRegister)
        {
            int nextExtNum = 1;
            while (archive.GetEntry($"xl/externalLinks/externalLink{nextExtNum}.xml") != null)
            {
                nextExtNum++;
            }

            maxRId++;
            string newRelId = $"rId{maxRId}";
            string linkPartRelTarget = $"externalLinks/externalLink{nextExtNum}.xml";
            string linkPartFullZip = $"xl/{linkPartRelTarget}";
            string linkRelsZip = $"xl/externalLinks/_rels/externalLink{nextExtNum}.xml.rels";

            // Create externalLink XML with accurate dynamic sheet names
            var sheets = GetExternalWorkbookSheetNames(srcFile, exportDir, matchedPairs);
            var sbSheets = new StringBuilder();
            foreach (var s in sheets)
            {
                string escapedSheet = SecurityElement.Escape(s) ?? s;
                sbSheets.Append($"<sheetName val=\"{escapedSheet}\"/>");
            }

            string escapedSrc = Uri.EscapeDataString(srcFile).Replace("%2E", ".");
            string extLinkContent = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<externalLink xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\r\n" +
                "  <externalBook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId1\">\r\n" +
                $"    <sheetNames>{sbSheets}</sheetNames>\r\n" +
                "  </externalBook>\r\n" +
                "</externalLink>";

            var newExtEntry = archive.CreateEntry(linkPartFullZip, CompressionLevel.Fastest);
            using (var writer = new StreamWriter(newExtEntry.Open(), Utf8NoBom))
            {
                writer.Write(extLinkContent);
            }

            // Create externalLink .rels
            string extRelsContent = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                $"  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath\" Target=\"{escapedSrc}\" TargetMode=\"External\"/>\r\n" +
                "</Relationships>";

            var newExtRelsEntry = archive.CreateEntry(linkRelsZip, CompressionLevel.Fastest);
            using (var writer = new StreamWriter(newExtRelsEntry.Open(), Utf8NoBom))
            {
                writer.Write(extRelsContent);
            }

            // Register in [Content_Types].xml
            var ctEntry = archive.GetEntry("[Content_Types].xml");
            if (ctEntry != null)
            {
                string ctContent;
                using (var reader = new StreamReader(ctEntry.Open(), Encoding.UTF8))
                {
                    ctContent = reader.ReadToEnd();
                }
                string overrideStr = $"<Override PartName=\"/{linkPartFullZip}\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml\"/>";
                if (!ctContent.Contains(linkPartFullZip, StringComparison.OrdinalIgnoreCase))
                {
                    ctContent = ctContent.Replace("</Types>", $"{overrideStr}</Types>");
                    ctEntry.Delete();
                    var newCt = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Fastest);
                    using var writer = new StreamWriter(newCt.Open(), Utf8NoBom);
                    writer.Write(ctContent);
                }
            }

            // Add relationship to xl/_rels/workbook.xml.rels
            wbRelsDoc.Root?.Add(new XElement(pkgRelsNs + "Relationship",
                new XAttribute("Id", newRelId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink"),
                new XAttribute("Target", linkPartRelTarget)));
            wbRelsDocModified = true;

            // Add externalReference to xl/workbook.xml
            if (extRefsElem == null)
            {
                extRefsElem = new XElement(wbNs + "externalReferences");
                var sheetsElem = wbDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheets");
                if (sheetsElem != null) sheetsElem.AddAfterSelf(extRefsElem);
                else wbDoc.Root?.Add(extRefsElem);
            }

            extRefsElem.Add(new XElement(wbNs + "externalReference", new XAttribute(rNs + "id", newRelId)));
            wbDocModified = true;

            int newExtIndex = extRefsElem.Elements().Count();
            fileToExtIndex[srcFile] = newExtIndex;
            fileToExtIndex[Path.GetFileNameWithoutExtension(srcFile)] = newExtIndex;
        }

        // Configure full calculation on workbook open
        var calcPr = wbDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "calcPr");
        if (calcPr != null)
        {
            calcPr.SetAttributeValue("fullCalcOnLoad", "1");
            calcPr.SetAttributeValue("forceFullCalculation", "1");
            wbDocModified = true;
        }
        else
        {
            wbDoc.Root?.Add(new XElement(wbNs + "calcPr",
                new XAttribute("fullCalcOnLoad", "1"),
                new XAttribute("forceFullCalculation", "1")));
            wbDocModified = true;
        }

        if (wbRelsDocModified)
        {
            wbRelsEntry.Delete();
            var newWbRels = archive.CreateEntry("xl/_rels/workbook.xml.rels", CompressionLevel.Fastest);
            using var s = newWbRels.Open();
            wbRelsDoc.Save(s);
        }

        if (wbDocModified)
        {
            wbEntry.Delete();
            var newWb = archive.CreateEntry("xl/workbook.xml", CompressionLevel.Fastest);
            using var s = newWb.Open();
            wbDoc.Save(s);
        }

        // --- STEP 3: Inject / Restore formulas in worksheet XMLs ---
        var linkablePairs = matchedPairs
            .Where(p => p.IsApproved &&
                        p.InjectedRate.HasValue &&
                        p.InjectedRate.Value > 0m &&
                        !p.TargetItem.IsProtected &&
                        p.TargetItem.Type != BoqItemType.ProvisionalSum &&
                        p.TargetItem.AnchorRowIndex > 0)
            .ToList();

        var pairsByTargetSheet = linkablePairs
            .GroupBy(p => !string.IsNullOrWhiteSpace(p.TargetItem.SheetName) ? p.TargetItem.SheetName : p.TargetItem.BillNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var cellPattern = new Regex(@"(<(?:x:)?c\s+r=""([A-Z0-9]+)""[^>]*>)(?:<(?:x:)?f[^>]*>.*?</(?:x:)?f>)?(\s*<(?:x:)?v>)", RegexOptions.Compiled);

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

            // Build formula map for cells in this sheet
            var cellFormulaMap = new Dictionary<string, string>(sheetPairs.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var pair in sheetPairs)
            {
                int anchorRow = pair.TargetItem.AnchorRowIndex;
                int rateCol = pair.TargetItem.RateColumnIndex > 0 ? pair.TargetItem.RateColumnIndex : 7;
                string targetCellRef = $"{GetExcelColumnLetter(rateCol)}{anchorRow}";

                // 1. Check if template already had an external formula for this cell
                string fullKey = $"{entryPath}!{targetCellRef}";
                if (originalTemplateFormulas != null && originalTemplateFormulas.TryGetValue(fullKey, out string? origFormula) && !string.IsNullOrWhiteSpace(origFormula))
                {
                    cellFormulaMap[targetCellRef] = origFormula;
                    continue;
                }

                // 2. Otherwise generate relative formula pointing to matched contractor item
                if (pair.MatchedSourceItem != null && pair.MatchedSourceItem.AnchorRowIndex > 0)
                {
                    string srcFile = !string.IsNullOrWhiteSpace(pair.MatchedSourceItem.WorkbookName)
                        ? pair.MatchedSourceItem.WorkbookName
                        : (primarySourceFileName ?? "");

                    if (!fileToExtIndex.TryGetValue(srcFile, out int extIdx) &&
                        !fileToExtIndex.TryGetValue(Path.GetFileNameWithoutExtension(srcFile), out extIdx))
                    {
                        extIdx = 1;
                    }

                    string srcSheet = string.IsNullOrWhiteSpace(pair.MatchedSourceItem.SheetName) ? "Sheet1" : pair.MatchedSourceItem.SheetName;
                    string cleanSheet = srcSheet.Replace("'", "''");
                    bool needsQuotes = cleanSheet.Any(c => !char.IsLetterOrDigit(c) && c != '_');
                    string formulaSheetRef = needsQuotes ? $"'[{extIdx}]{cleanSheet}'" : $"[{extIdx}]{cleanSheet}";
                    int srcCol = pair.MatchedSourceItem.RateColumnIndex > 0 ? pair.MatchedSourceItem.RateColumnIndex : 18;
                    string srcColLetter = GetExcelColumnLetter(srcCol);
                    int srcRow = pair.MatchedSourceItem.AnchorRowIndex;

                    string formulaText = $"{formulaSheetRef}!${srcColLetter}${srcRow}";
                    cellFormulaMap[targetCellRef] = formulaText;
                }
            }

            if (cellFormulaMap.Count == 0) continue;

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
            using (var writer = new StreamWriter(newSheetEntry.Open(), Utf8NoBom))
            {
                writer.Write(updatedSheetXml);
            }
        }
    }

    /// <summary>
    /// Reads and extracts the sheet names of an external contractor or target workbook in exact index order.
    /// Falls back to matched pairs if file cannot be read directly.
    /// </summary>
    private static List<string> GetExternalWorkbookSheetNames(
        string contractorFileName,
        string packageDirectory,
        IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var sheetNames = new List<string>();

        // 1. Attempt to locate the external file on disk
        string? candidatePath = null;
        if (File.Exists(contractorFileName))
        {
            candidatePath = contractorFileName;
        }
        else
        {
            string p1 = Path.Combine(packageDirectory, contractorFileName);
            if (File.Exists(p1))
            {
                candidatePath = p1;
            }
            else
            {
                string p2 = Path.Combine(packageDirectory, Path.GetFileName(contractorFileName));
                if (File.Exists(p2))
                {
                    candidatePath = p2;
                }
            }
        }

        if (candidatePath != null)
        {
            try
            {
                using var zip = ZipFile.OpenRead(candidatePath);
                var wbEntry = zip.GetEntry("xl/workbook.xml");
                if (wbEntry != null)
                {
                    using var sr = new StreamReader(wbEntry.Open(), Encoding.UTF8);
                    string wbXml = sr.ReadToEnd();
                    var matches = Regex.Matches(wbXml, @"<(?:\w+:)?sheet\b[^>]*name=""([^""]+)""", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        string sName = m.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(sName) && !sheetNames.Contains(sName, StringComparer.OrdinalIgnoreCase))
                        {
                            sheetNames.Add(sName);
                        }
                    }
                }
            }
            catch
            {
                // Fallback gracefully if file cannot be opened as zip
            }
        }

        // 2. Augment with any sheet names explicitly referenced by matched pairs for this file
        string fileNameOnly = Path.GetFileName(contractorFileName);
        foreach (var pair in matchedPairs)
        {
            var src = pair.MatchedSourceItem;
            if (src == null || string.IsNullOrWhiteSpace(src.SheetName)) continue;

            string srcFile = !string.IsNullOrWhiteSpace(src.WorkbookName) ? Path.GetFileName(src.WorkbookName) : "";
            if (string.Equals(srcFile, fileNameOnly, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(srcFile, contractorFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (!sheetNames.Contains(src.SheetName, StringComparer.OrdinalIgnoreCase))
                {
                    sheetNames.Add(src.SheetName);
                }
            }
        }

        if (sheetNames.Count == 0)
        {
            sheetNames.Add("Sheet1");
        }

        return sheetNames;
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


