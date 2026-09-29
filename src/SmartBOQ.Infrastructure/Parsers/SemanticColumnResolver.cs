using System.Text.RegularExpressions;
using ExcelDataReader;

namespace SmartBOQ.Infrastructure.Parsers;

/// <summary>
/// Resolved physical column coordinates for an arbitrary Bill of Quantities spreadsheet.
/// Completely decoupled from any specific template, file name, or layout standard.
/// </summary>
public sealed record ResolvedBoqColumns
{
    public int BillColumn { get; init; } = -1;
    public int SectionColumn { get; init; } = -1;
    public int ItemCodeColumn { get; init; } = -1;
    public int SerialNumberColumn { get; init; } = -1;
    public IReadOnlyList<int> HierarchyColumns { get; init; } = Array.Empty<int>();
    public int DescriptionColumn { get; init; } = -1;
    public int UnitColumn { get; init; } = -1;
    public int QuantityColumn { get; init; } = -1;
    public int RateColumn { get; init; } = -1;
    public int TotalAmountColumn { get; init; } = -1;
    public int NumberOffColumn { get; init; } = -1;
    public int NoteColumn { get; init; } = -1;
    public int HeaderRowIndex { get; init; } = 1;
    public string DetectedCurrency { get; init; } = "EGP";
    public bool HasDetectedHeaders { get; init; }
}

/// <summary>
/// Universal AI-inspired Semantic Column Resolver.
/// Automatically detects and binds BOQ columns across arbitrary files, languages (Arabic, English, French),
/// and formats using dictionary heuristics and cell-type validation.
/// </summary>
public static class SemanticColumnResolver
{
    // Canonical regex patterns for multilingual BOQ headers
    private static readonly Regex SerialNumberPattern = new(
        @"^(sn|s\.n\.?|ser|no\.?|seq|م|مسلسل|تسلسل|م\.)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ItemCodePattern = new(
        @"^(item\s*(no|code|#)?|item|code|ref|pos|line|رقم|كود|كود\s*البند|رقم\s*البند|بند)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HierarchyLevelPattern = new(
        @"^(level\s*\d+|\d+|division|package|boq\s*name|sub-?boq(\s*name)?|cat\.?|category|sub-?category|المرحلة|الباكج)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionPattern = new(
        @"(description|particular|statement|scope|work|specification|details|item\s*desc|الوصف|البيان|تفاصيل|تفاصيل\s*البند|بيان\s*الأعمال|المواصفات)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnitPattern = new(
        @"(^|\b)(unit|uom|measure|u\.?o\.?m\.?|unit\s*of\s*measure|unité|الوحدة|وحدة\s*القياس|المقياس)($|\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuantityPattern = new(
        @"(quantity|qty|vol|volume|quantities|qte|quantité|الكمية|الكميات|إجمالي\s*الكمية)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RatePattern = new(
        @"(unit\s*rate|unit\s*price|rate|price|net\s*rate|tender\s*rate|p\.?u\.?|(^|\b)u\.?r\.?($|\b)|prix\s*unitaire|سعر\s*الوحدة|الفئة|فئة|السعر|سعر\s*البند|سعر\s*إفرادي|سعر\s*مفرد)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AmountPattern = new(
        @"(total\s*amount|total\s*price|amount|total|net\s*amount|extended|montant|الإجمالي|المبلغ|القيمة|إجمالي\s*القيمة|جملة)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BillPattern = new(
        @"^(bill|bill\s*no\.?|trade|package|division|الباب|القسم|المرحلة|العقد|الباكج)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SectionPattern = new(
        @"^(section|sub-?section|class|part|heading|عنصر|بند\s*رئيسي|مجموعة)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Checks whether candidate rows belong to an Excel Pivot Table or summary aggregation sheet.
    /// </summary>
    public static bool IsPivotOrSummarySheet(IReadOnlyList<string[]> scannedRows)
    {
        if (scannedRows == null || scannedRows.Count == 0) return true;

        bool hasPivotLabels = false;
        bool hasAggregationHeaders = false;

        foreach (var row in scannedRows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                string cell = row[c];
                if (string.IsNullOrWhiteSpace(cell)) continue;

                if (cell.Contains("Row Labels", StringComparison.OrdinalIgnoreCase) ||
                    cell.Contains("تسميات الصفوف", StringComparison.OrdinalIgnoreCase) ||
                    cell.Equals("Pivot Table", StringComparison.OrdinalIgnoreCase) ||
                    cell.Equals("PivotTable", StringComparison.OrdinalIgnoreCase))
                {
                    hasPivotLabels = true;
                }

                if (cell.Contains("Sum of ", StringComparison.OrdinalIgnoreCase) ||
                    cell.Contains("Count of ", StringComparison.OrdinalIgnoreCase) ||
                    cell.Contains("Average of ", StringComparison.OrdinalIgnoreCase) ||
                    cell.Contains("مجموع ال", StringComparison.OrdinalIgnoreCase))
                {
                    hasAggregationHeaders = true;
                }
            }
        }

        return hasPivotLabels || hasAggregationHeaders;
    }

    /// <summary>
    /// Scans the first candidate rows of an Excel sheet to determine column layout.
    /// </summary>
    public static ResolvedBoqColumns ResolveColumns(IExcelDataReader reader, int maxScanRows = 35)
    {
        var scannedRows = new List<string[]>(maxScanRows);
        int r = 0;

        while (reader.Read() && r < maxScanRows)
        {
            r++;
            var rowVals = new string[reader.FieldCount];
            for (int c = 0; c < reader.FieldCount; c++)
            {
                rowVals[c] = reader.GetValue(c)?.ToString()?.Trim() ?? string.Empty;
            }
            scannedRows.Add(rowVals);
        }

        return ResolveColumnsFromRows(scannedRows, reader.FieldCount);
    }

    /// <summary>
    /// Resolves BOQ column coordinates dynamically from an in-memory buffer of scanned rows.
    /// </summary>
    public static ResolvedBoqColumns ResolveColumnsFromRows(IReadOnlyList<string[]> scannedRows, int fieldCount)
    {
        int colCode = -1, colSN = -1, colDesc = -1, colUnit = -1, colQty = -1;
        int colRate = -1, colAmt = -1, colBill = -1, colSec = -1;
        var hierarchyCols = new List<int>();
        int headerRow = 1;
        string currency = "EGP";
        bool foundHeader = false;

        if (scannedRows == null || scannedRows.Count == 0)
        {
            return new ResolvedBoqColumns();
        }

        // 1. Scan rows to identify header row with maximum keyword matches
        int bestMatchCount = 0;
        int bestRowIdx = 0;

        for (int i = 0; i < scannedRows.Count; i++)
        {
            var row = scannedRows[i];
            int matches = 0;

            for (int c = 0; c < row.Length; c++)
            {
                string text = row[c];
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (text.Contains("USD", StringComparison.OrdinalIgnoreCase) || text.Contains("($)")) currency = "USD";
                else if (text.Contains("EUR", StringComparison.OrdinalIgnoreCase) || text.Contains("(€)")) currency = "EUR";

                if (ItemCodePattern.IsMatch(text) || SerialNumberPattern.IsMatch(text) || DescriptionPattern.IsMatch(text) ||
                    UnitPattern.IsMatch(text) || QuantityPattern.IsMatch(text) ||
                    RatePattern.IsMatch(text) || AmountPattern.IsMatch(text))
                {
                    matches++;
                }
            }

            if (matches > bestMatchCount)
            {
                bestMatchCount = matches;
                bestRowIdx = i;
            }
        }

        if (bestMatchCount >= 2)
        {
            foundHeader = true;
            headerRow = bestRowIdx + 1;

            // Inspect primary header row
            AssignRowColumns(scannedRows[bestRowIdx], ref colRate, ref colAmt, ref colQty, ref colUnit, ref colDesc, ref colCode, ref colSN, ref colBill, ref colSec, hierarchyCols);

            // Inspect adjacent sub-header row (for merged or multi-line table headers)
            if (bestRowIdx + 1 < scannedRows.Count)
            {
                AssignRowColumns(scannedRows[bestRowIdx + 1], ref colRate, ref colAmt, ref colQty, ref colUnit, ref colDesc, ref colCode, ref colSN, ref colBill, ref colSec, hierarchyCols);
            }
            if (bestRowIdx > 0)
            {
                AssignRowColumns(scannedRows[bestRowIdx - 1], ref colRate, ref colAmt, ref colQty, ref colUnit, ref colDesc, ref colCode, ref colSN, ref colBill, ref colSec, hierarchyCols);
            }
        }

        // 2. Data-type heuristic fallback for any unresolved columns
        if (scannedRows.Count > headerRow)
        {
            for (int c = 0; c < fieldCount; c++)
            {
                int textLengthSum = 0;
                int numericCount = 0;
                int unitMatchCount = 0;
                int billMatchCount = 0;
                int sectionMatchCount = 0;

                for (int rowIdx = headerRow; rowIdx < scannedRows.Count; rowIdx++)
                {
                    string val = scannedRows[rowIdx][c];
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    textLengthSum += val.Length;
                    if (decimal.TryParse(val, out _)) numericCount++;
                    if (IsKnownUnitToken(val)) unitMatchCount++;

                    if (val.StartsWith("Bill ", StringComparison.OrdinalIgnoreCase) ||
                        val.StartsWith("Schedule ", StringComparison.OrdinalIgnoreCase) ||
                        val.StartsWith("الباب ", StringComparison.OrdinalIgnoreCase) ||
                        val.StartsWith("جدول ", StringComparison.OrdinalIgnoreCase))
                    {
                        billMatchCount++;
                    }
                    else if (val.StartsWith("Section ", StringComparison.OrdinalIgnoreCase) ||
                             val.StartsWith("قسم ", StringComparison.OrdinalIgnoreCase) ||
                             val.StartsWith("بند رئيسي", StringComparison.OrdinalIgnoreCase))
                    {
                        sectionMatchCount++;
                    }
                }

                // If description is missing and column has long descriptive text
                if (colDesc < 0 && textLengthSum > 60 && numericCount == 0 && billMatchCount == 0)
                {
                    colDesc = c;
                }
                // If unit is missing and column contains recognized engineering units
                else if (colUnit < 0 && unitMatchCount >= 2)
                {
                    colUnit = c;
                }
                // If Bill column is missing and column contains "Bill X" partitions
                if (colBill < 0 && billMatchCount >= 2)
                {
                    colBill = c;
                }
                // If Section column is missing and column contains "Section X"
                if (colSec < 0 && sectionMatchCount >= 2)
                {
                    colSec = c;
                }
            }
        }

        // 3. Fallback to sensible defaults or dynamic search if still absent
        if (colUnit < 0 && scannedRows.Count > headerRow)
        {
            for (int c = 0; c < fieldCount; c++)
            {
                int unitCount = 0;
                for (int rowIdx = headerRow; rowIdx < scannedRows.Count; rowIdx++)
                {
                    if (IsKnownUnitToken(scannedRows[rowIdx][c])) unitCount++;
                }
                if (unitCount >= 1)
                {
                    colUnit = c;
                    break;
                }
            }
        }

        // Auto-detect hierarchy columns to the left of the item code / description
        if (hierarchyCols.Count == 0 && colDesc > 1)
        {
            int limit = colCode >= 0 ? Math.Min(colDesc, colCode) : colDesc;
            for (int c = 0; c < limit; c++)
            {
                if (c != colSN && c != colCode && c != colBill && c != colSec)
                {
                    hierarchyCols.Add(c);
                }
            }
        }

        if (colCode < 0 && colSN >= 0 && !hierarchyCols.Contains(colSN)) colCode = colSN;
        if (colSN < 0 && colCode >= 0 && !hierarchyCols.Contains(colCode)) colSN = colCode;

        if (colDesc < 0) colDesc = 2;
        if (colQty < 0) colQty = 4;
        if (colUnit < 0) colUnit = 5;
        if (colRate < 0) colRate = 6;
        if (colAmt < 0) colAmt = 7;
        if (colCode < 0 && !hierarchyCols.Contains(0)) colCode = 0;
        if (colCode >= 0 && hierarchyCols.Contains(colCode)) colCode = -1;

        return new ResolvedBoqColumns
        {
            BillColumn = colBill,
            SectionColumn = colSec,
            ItemCodeColumn = colCode,
            SerialNumberColumn = colSN,
            HierarchyColumns = hierarchyCols.Distinct().OrderBy(x => x).ToArray(),
            DescriptionColumn = colDesc,
            UnitColumn = colUnit,
            QuantityColumn = colQty,
            RateColumn = colRate,
            TotalAmountColumn = colAmt,
            HeaderRowIndex = headerRow,
            DetectedCurrency = currency,
            HasDetectedHeaders = foundHeader
        };
    }

    private static void AssignRowColumns(
        string[] row,
        ref int colRate,
        ref int colAmt,
        ref int colQty,
        ref int colUnit,
        ref int colDesc,
        ref int colCode,
        ref int colSN,
        ref int colBill,
        ref int colSec,
        List<int> hierarchyCols)
    {
        for (int c = 0; c < row.Length; c++)
        {
            string text = row[c];
            if (string.IsNullOrWhiteSpace(text)) continue;

            if ((text.Equals("U.R", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("UR", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Uplifted unit rate", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Unit Rate\nUplifted", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("CCC Unit Rate Uplifted", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("Unit Rate", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("Tender Rate", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("سعر الوحدة", StringComparison.OrdinalIgnoreCase)) && !text.Contains("total", StringComparison.OrdinalIgnoreCase) && !text.Contains("amount", StringComparison.OrdinalIgnoreCase))
            {
                colRate = c; // Highest fidelity: explicit unit rate column
            }
            else if (RatePattern.IsMatch(text) && colRate < 0 && !text.Contains("total", StringComparison.OrdinalIgnoreCase) && !text.Contains("amount", StringComparison.OrdinalIgnoreCase))
            {
                colRate = c;
            }
            else if (AmountPattern.IsMatch(text) && colAmt < 0) colAmt = c;
            else if (QuantityPattern.IsMatch(text) && colQty < 0) colQty = c;
            else if (UnitPattern.IsMatch(text) && colUnit < 0 && !text.Contains("rate", StringComparison.OrdinalIgnoreCase) && !text.Contains("price", StringComparison.OrdinalIgnoreCase)) colUnit = c;
            else if (DescriptionPattern.IsMatch(text) && colDesc < 0) colDesc = c;
            else if (SerialNumberPattern.IsMatch(text) && colSN < 0) colSN = c;
            else if (ItemCodePattern.IsMatch(text) && colCode < 0) colCode = c;
            else if (BillPattern.IsMatch(text) && colBill < 0) colBill = c;
            else if (SectionPattern.IsMatch(text) && colSec < 0) colSec = c;
            else if (HierarchyLevelPattern.IsMatch(text) && !hierarchyCols.Contains(c))
            {
                hierarchyCols.Add(c);
            }
        }
    }

    private static bool IsKnownUnitToken(string val)
    {
        string v = val.Trim().ToLowerInvariant();
        return v is "m2" or "m3" or "lm" or "m" or "nr" or "no" or "nos" or "item" or "ton" or "kg" or "ls" or "sum"
            or "sqm" or "cum" or "lin.m" or "mtr"
            or "م2" or "م²" or "م3" or "م³" or "م.ط" or "متر" or "متر مربع" or "متر مكعب" or "متر طولي"
            or "عدد" or "بند" or "حبة" or "قطعة" or "طن" or "كجم" or "مقطوعية" or "مقطوع" or "جملة";
    }
}
