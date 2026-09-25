using System.Text;
using ExcelDataReader;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;

namespace SmartBOQ.Infrastructure.Parsers;

/// <summary>
/// Hierarchical multi-sheet BOQ reader that reconstructs broken multi-row line items
/// using a deterministic Finite State Machine (FSM).
/// Inherits from <see cref="BaseBoqReader"/>.
/// </summary>
public sealed class HierarchicalBoqReader : BaseBoqReader
{
    private static readonly string[] NonBillSheetPrefixes =
    [
        "TABLE OF CONTENTS",
        "PREAMBLE",
        "SCHEDULE OF INSURANCE",
        "INSTRUCTION",
        "GRAND SUMMARY",
        "COVER",
        "DAYWORKS",
        "PRICE ANALYSIS",
        "AUDIT",
        "DASHBOARD",
        "EXECUTIVE",
        "PRICING_LINKAGE",
        "LINKAGE"
    ];

    public override async Task<IReadOnlyList<BoqSheet>> ReadConsultantHierarchicalBoqAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Consultant pricing schedule file not found.", filePath);
        }

        return await Task.Run(() =>
        {
            var sheets = new List<BoqSheet>(35);

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                ct.ThrowIfCancellationRequested();

                string sheetName = reader.Name?.Trim() ?? string.Empty;

                // Skip non-bill summary/preamble sheets
                if (IsNonBillSheet(sheetName))
                {
                    continue;
                }

                bool isPsSheet = sheetName.Contains("Provisional", StringComparison.OrdinalIgnoreCase) || 
                                 sheetName.EndsWith("PS", StringComparison.OrdinalIgnoreCase);

                var items = new List<BoqItem>(150);
                string currentSection = string.Empty;
                string currentItemCode = string.Empty;
                var descBuilder = new StringBuilder(512);
                int startRowIndex = 0;
                string detectedCurrency = "EGP";

                // Dynamic Header Column Mapping (defaults to standard Consultant format)
                int colItem = 0;
                int colDesc = 2;
                int colQty = 4;
                int colUnit = 5;
                int colRate = 6;
                int colAmt = 7;
                bool headerFound = false;

                int rowIndex = 0;
                while (reader.Read())
                {
                    rowIndex++;

                    // Scan for dynamic column headers in the first 25 rows
                    if (!headerFound && rowIndex <= 25)
                    {
                        int tItem = -1, tDesc = -1, tQty = -1, tUnit = -1, tRate = -1, tAmt = -1;
                        for (int c = 0; c < reader.FieldCount; c++)
                        {
                            string cellVal = reader.GetValue(c)?.ToString()?.Trim() ?? string.Empty;
                            if (cellVal.Equals("USD", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("(USD)", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("RATE (USD)", StringComparison.OrdinalIgnoreCase)) detectedCurrency = "USD";
                            else if (cellVal.Equals("EUR", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("(EUR)", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("€")) detectedCurrency = "EUR";

                            if (cellVal.Equals("ITEM", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("ITEM NO.", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("ITEM NO", StringComparison.OrdinalIgnoreCase)) tItem = c;
                            else if (cellVal.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("PARTICULAR", StringComparison.OrdinalIgnoreCase)) tDesc = c;
                            else if (cellVal.StartsWith("QTY", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("QUANTITY", StringComparison.OrdinalIgnoreCase)) tQty = c;
                            else if (cellVal.Equals("UNIT", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("UOM", StringComparison.OrdinalIgnoreCase)) tUnit = c;
                            else if (cellVal.StartsWith("RATE", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("PRICE", StringComparison.OrdinalIgnoreCase)) tRate = c;
                            else if (cellVal.StartsWith("AMOUNT", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase)) tAmt = c;
                        }

                        if (tUnit >= 0 && (tQty >= 0 || tDesc >= 0))
                        {
                            if (tItem >= 0) colItem = tItem;
                            if (tDesc >= 0) colDesc = tDesc;
                            if (tUnit >= 0) colUnit = tUnit;
                            if (tQty >= 0) colQty = tQty;
                            if (tRate >= 0) colRate = tRate;
                            if (tAmt >= 0) colAmt = tAmt;
                            headerFound = true;
                            continue; // Skip header row
                        }
                    }

                    // Safe cell access via dynamic column mapping
                    string colCode = GetSafeString(reader, colItem);
                    string colText = GetSafeString(reader, colDesc);
                    string colUnitRaw = colUnit >= 0 ? GetSafeString(reader, colUnit) : string.Empty;
                    string normUnit = NormalizeUnit(colUnitRaw);
                    object? colQtyObj = colQty >= 0 ? GetSafeValue(reader, colQty) : null;
                    decimal qty = ParseDecimal(colQtyObj);
                    object? colRateObj = colRate >= 0 ? GetSafeValue(reader, colRate) : null;
                    string colAmtStr = colAmt >= 0 ? GetSafeString(reader, colAmt) : string.Empty;

                    bool isRateOnly = colAmtStr.Contains("Rate only", StringComparison.OrdinalIgnoreCase) ||
                                      colUnitRaw.Contains("Rate only", StringComparison.OrdinalIgnoreCase);

                    // Section Detection: Only when no item code, no unit, and no qty
                    if (string.IsNullOrWhiteSpace(colCode) && qty == 0m && string.IsNullOrWhiteSpace(normUnit))
                    {
                        if (colText.StartsWith("SECTION", StringComparison.OrdinalIgnoreCase) || 
                            colText.StartsWith("BILL NO", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("PART ", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("CLASS ", StringComparison.OrdinalIgnoreCase))
                        {
                            currentSection = colText;
                            continue;
                        }
                    }

                    // Item Code boundary detected in Col A
                    if (!string.IsNullOrWhiteSpace(colCode) && colCode.Length <= 8 && !colCode.Equals("ITEM", StringComparison.OrdinalIgnoreCase))
                    {
                        currentItemCode = colCode;
                        descBuilder.Clear();
                        startRowIndex = rowIndex;
                    }

                    // Append description segment
                    if (!string.IsNullOrWhiteSpace(colText) && !colText.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase))
                    {
                        if (startRowIndex == 0) startRowIndex = rowIndex;
                        if (descBuilder.Length > 0) descBuilder.Append(' ');
                        descBuilder.Append(colText);
                    }

                    // Anchor Row Detection:
                    // 1. Has valid numeric Quantity AND valid Unit (standard item)
                    // 2. Has valid Unit AND non-empty ItemCode (Rate-Only civil items like probing/grouting in Infra)
                    // 3. Explicit Rate-Only note
                    bool hasValidUnit = !string.IsNullOrWhiteSpace(normUnit) && !normUnit.Equals("unit", StringComparison.OrdinalIgnoreCase);
                    bool isAnchorRow = (qty > 0m && hasValidUnit) || 
                                       (hasValidUnit && !string.IsNullOrWhiteSpace(currentItemCode)) ||
                                       (hasValidUnit && isRateOnly);

                    if (isAnchorRow)
                    {
                        decimal? rate = null;
                        decimal parsedRate = ParseDecimal(colRateObj);
                        if (parsedRate > 0m) rate = parsedRate;

                        string fullDescription = CompactStringPool.Shared.GetOrAdd(descBuilder.ToString().Trim());
                        if (string.IsNullOrWhiteSpace(fullDescription))
                        {
                            fullDescription = colText;
                        }

                        var itemType = isPsSheet 
                            ? BoqItemType.ProvisionalSum 
                            : (isRateOnly || qty == 0m ? BoqItemType.RateOnly : BoqItemType.Normal);

                        var item = new BoqItem
                        {
                            Id = $"B_{sheetName}_{rowIndex}_{currentItemCode}",
                            BillNumber = sheetName,
                            SectionName = currentSection,
                            ItemCode = currentItemCode,
                            Description = fullDescription,
                            NormalizedDescription = NormalizeDescription(fullDescription),
                            Unit = normUnit,
                            Quantity = qty,
                            UnitRate = rate,
                            TotalAmount = rate.HasValue ? rate.Value * qty : null,
                            NumberOff = 1,
                            Currency = detectedCurrency,
                            Type = itemType,
                            SheetName = sheetName,
                            StartRowIndex = startRowIndex > 0 ? startRowIndex : rowIndex,
                            AnchorRowIndex = rowIndex,
                            RateColumnIndex = colRate >= 0 ? colRate + 1 : 7,
                            QuantityColumnIndex = colQty >= 0 ? colQty + 1 : 5,
                            AmountColumnIndex = colAmt >= 0 ? colAmt + 1 : 8,
                            TableStartColumnIndex = 1, // Column A (Item code)
                            TableEndColumnIndex = colAmt >= 0 ? colAmt + 1 : 8 // Column H (Total amount)
                        };

                        items.Add(item);

                        // Reset pending item state
                        descBuilder.Clear();
                        currentItemCode = string.Empty;
                        startRowIndex = 0;
                    }
                }

                var sheet = new BoqSheet
                {
                    SheetName = sheetName,
                    BillCode = ExtractBillCode(sheetName),
                    Items = items,
                    IsProvisionalSumSheet = isPsSheet,
                    Currency = detectedCurrency
                };

                sheets.Add(sheet);

            } while (reader.NextResult());

            return (IReadOnlyList<BoqSheet>)sheets;
        }, ct);
    }

    private static bool IsNonBillSheet(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string trimmed = name.Trim();
        if (NonBillSheetPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (trimmed.Contains("Sum-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Summary", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string ExtractBillCode(string sheetName)
    {
        int dashIdx = sheetName.IndexOf('-');
        return dashIdx > 0 ? sheetName[..dashIdx].Trim() : sheetName.Trim();
    }
}
