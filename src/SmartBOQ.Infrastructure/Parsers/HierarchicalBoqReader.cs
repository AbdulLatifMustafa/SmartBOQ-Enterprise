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

                bool isPurePsSchedule = sheetName.Equals("Bill 6 Provisional Sum", StringComparison.OrdinalIgnoreCase) ||
                                        sheetName.StartsWith("Bill 6 Provisional", StringComparison.OrdinalIgnoreCase) ||
                                        sheetName.Equals("Provisional Sums", StringComparison.OrdinalIgnoreCase) ||
                                        sheetName.Equals("المبالغ الاحتياطية", StringComparison.OrdinalIgnoreCase);

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

                            if (cellVal.Equals("ITEM", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("ITEM NO.", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("ITEM NO", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("رقم", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("كود", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("م", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("بند", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("مسلسل", StringComparison.OrdinalIgnoreCase)) tItem = c;
                            else if (cellVal.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("PARTICULAR", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("الوصف", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("البيان", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("تفاصيل", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("المواصفات", StringComparison.OrdinalIgnoreCase)) tDesc = c;
                            else if (cellVal.StartsWith("QTY", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("QUANTITY", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الكمية", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الكميات", StringComparison.OrdinalIgnoreCase)) tQty = c;
                            else if (cellVal.Equals("UNIT", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("UOM", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("الوحدة", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("وحدة", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("وحدة القياس", StringComparison.OrdinalIgnoreCase)) tUnit = c;
                            else if (cellVal.StartsWith("RATE", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("PRICE", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("السعر", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الفئة", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("سعر", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("فئة", StringComparison.OrdinalIgnoreCase)) tRate = c;
                            else if (cellVal.StartsWith("AMOUNT", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الإجمالي", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("القيمة", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("المبلغ", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("جملة", StringComparison.OrdinalIgnoreCase)) tAmt = c;
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
                            colText.StartsWith("CLASS ", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("قسم", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("باب", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("بند رئيسي", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("أعمال", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("مجموعة", StringComparison.OrdinalIgnoreCase))
                        {
                            currentSection = colText;
                            continue;
                        }
                    }

                    // Item Code boundary detected in Col A
                    if (!string.IsNullOrWhiteSpace(colCode) && colCode.Length <= 8 && !colCode.Equals("ITEM", StringComparison.OrdinalIgnoreCase) && !colCode.Equals("م", StringComparison.OrdinalIgnoreCase) && !colCode.Equals("بند", StringComparison.OrdinalIgnoreCase))
                    {
                        currentItemCode = colCode;
                        descBuilder.Clear();
                        startRowIndex = rowIndex;
                    }

                    // Append description segment
                    if (!string.IsNullOrWhiteSpace(colText) && !colText.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase) && !colText.Equals("الوصف", StringComparison.OrdinalIgnoreCase) && !colText.Equals("البيان", StringComparison.OrdinalIgnoreCase))
                    {
                        if (startRowIndex == 0) startRowIndex = rowIndex;
                        if (descBuilder.Length > 0) descBuilder.Append(' ');
                        descBuilder.Append(colText);
                    }

                    // Anchor Row Detection:
                    // 1. Has valid numeric Quantity AND valid Unit (standard item)
                    // 2. Has valid Unit AND non-empty ItemCode (Rate-Only civil items like probing/grouting in Infra)
                    // 3. Explicit Rate-Only note
                    bool hasValidUnit = !string.IsNullOrWhiteSpace(normUnit) && !normUnit.Equals("unit", StringComparison.OrdinalIgnoreCase) && !normUnit.Equals("الوحدة", StringComparison.OrdinalIgnoreCase);
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

                        bool isItemPs = isPurePsSchedule ||
                                        fullDescription.Contains("provisional sum", StringComparison.OrdinalIgnoreCase) ||
                                        fullDescription.Contains("مبلغ احتياطي", StringComparison.OrdinalIgnoreCase) ||
                                        fullDescription.Contains("مبالغ احتياطية", StringComparison.OrdinalIgnoreCase);

                        var itemType = isItemPs 
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
                    IsProvisionalSumSheet = isPurePsSchedule,
                    Currency = detectedCurrency
                };

                sheets.Add(sheet);

            } while (reader.NextResult());

            return (IReadOnlyList<BoqSheet>)sheets;
        }, ct);
    }


    private static string ExtractBillCode(string sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName) || IsNonBillSheet(sheetName))
            return "-";

        // 1. Explicit keyword match
        var matchKeyword = System.Text.RegularExpressions.Regex.Match(sheetName, @"(?:Bill|Schedule|Package|الباب|جدول)\s*([0-9]+[a-zA-Z]*|[a-zA-Z][0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (matchKeyword.Success)
        {
            return matchKeyword.Groups[1].Value.ToUpperInvariant();
        }

        // 2. Leading alphanumeric code
        var matchLeading = System.Text.RegularExpressions.Regex.Match(sheetName, @"^\s*([0-9]+(?:\.[0-9]+)?[a-zA-Z]?|[a-zA-Z][0-9]+)\b");
        if (matchLeading.Success)
        {
            return matchLeading.Groups[1].Value.ToUpperInvariant();
        }

        int dashIdx = sheetName.IndexOf('-');
        if (dashIdx > 0)
        {
            string prefix = sheetName[..dashIdx].Trim();
            if (prefix.Length <= 6 && prefix.Any(char.IsDigit))
            {
                return prefix.ToUpperInvariant();
            }
        }

        return "-";
    }
}
