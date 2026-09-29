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

                // Skip non-bill summary/preamble sheets (unless it is the only sheet in the workbook)
                if (reader.ResultsCount > 1 && IsNonBillSheet(sheetName))
                {
                    continue;
                }

                bool isPurePsSchedule = sheetName.Contains("Provisional Sum", StringComparison.OrdinalIgnoreCase) ||
                                        sheetName.Contains("Provisional", StringComparison.OrdinalIgnoreCase) ||
                                        sheetName.Contains("Contingenc", StringComparison.OrdinalIgnoreCase) ||
                                        sheetName.Contains("الاحتياطية", StringComparison.OrdinalIgnoreCase) ||
                                        sheetName.Contains("احتياطي", StringComparison.OrdinalIgnoreCase);

                var items = new List<BoqItem>(150);
                string currentSection = string.Empty;
                string currentItemCode = string.Empty;
                string currentSerialNumber = string.Empty;
                var descBuilder = new StringBuilder(512);
                int startRowIndex = 0;
                string detectedCurrency = "EGP";

                // Dynamic Header Column Mapping (defaults to standard Consultant format)
                int colSn = -1;
                int colItem = -1;
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

                    // Scan for dynamic column headers in the first 60 rows
                    if (!headerFound)
                    {
                        if (rowIndex <= 60)
                        {
                            int tSn = -1, tItem = -1, tDesc = -1, tQty = -1, tUnit = -1, tRate = -1, tAmt = -1;
                            for (int c = 0; c < reader.FieldCount; c++)
                            {
                                string cellVal = reader.GetValue(c)?.ToString()?.Trim() ?? string.Empty;
                                if (cellVal.Equals("USD", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("(USD)", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("RATE (USD)", StringComparison.OrdinalIgnoreCase)) detectedCurrency = "USD";
                                else if (cellVal.Equals("EUR", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("(EUR)", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("€")) detectedCurrency = "EUR";

                                if (cellVal.Equals("SN", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("S/N", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("S.N.", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("S.N", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("S.NO", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("S.NO.", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("مسلسل", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("م", StringComparison.OrdinalIgnoreCase))
                                {
                                    tSn = c;
                                }
                                else if (cellVal.Equals("ITEM", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("ITEM NO.", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("ITEM NO", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("ITEM CODE", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("رقم", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("كود", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("بند", StringComparison.OrdinalIgnoreCase))
                                {
                                    tItem = c;
                                }
                                else if (cellVal.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("PARTICULAR", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("الوصف", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("البيان", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("تفاصيل", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("المواصفات", StringComparison.OrdinalIgnoreCase))
                                {
                                    tDesc = c;
                                }
                                else if (cellVal.StartsWith("QTY", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("QUANTITY", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الكمية", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الكميات", StringComparison.OrdinalIgnoreCase))
                                {
                                    tQty = c;
                                }
                                else if (cellVal.Equals("UNIT", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("UOM", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("الوحدة", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("وحدة", StringComparison.OrdinalIgnoreCase) || cellVal.Equals("وحدة القياس", StringComparison.OrdinalIgnoreCase))
                                {
                                    tUnit = c;
                                }
                                else if (cellVal.StartsWith("RATE", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("PRICE", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("السعر", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الفئة", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("سعر", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("فئة", StringComparison.OrdinalIgnoreCase))
                                {
                                    tRate = c;
                                }
                                else if (cellVal.StartsWith("AMOUNT", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("الإجمالي", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("القيمة", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("المبلغ", StringComparison.OrdinalIgnoreCase) || cellVal.StartsWith("جملة", StringComparison.OrdinalIgnoreCase))
                                {
                                    tAmt = c;
                                }
                            }

                            if ((tUnit >= 0 && (tQty >= 0 || tDesc >= 0)) || (tDesc >= 0 && tQty >= 0 && (tRate >= 0 || tAmt >= 0)))
                            {
                                if (tItem >= 0) colItem = tItem;
                                if (tSn >= 0) colSn = tSn;
                                if (colItem == -1 && colSn >= 0) colItem = colSn;
                                if (colSn == -1 && colItem > 0) colSn = 0;
                                if (colItem == -1 && colSn == -1) colItem = 0;
                                if (tDesc >= 0) colDesc = tDesc;
                                if (tUnit >= 0) colUnit = tUnit;
                                if (tQty >= 0) colQty = tQty;
                                if (tRate >= 0) colRate = tRate;
                                if (tAmt >= 0) colAmt = tAmt;
                                headerFound = true;
                                continue; // Skip header row
                            }

                            // Strict Pre-Header Shield: Never parse document cover / title rows before header is found!
                            continue;
                        }
                        else
                        {
                            // If after 60 rows no valid header was found, skip this non-bill sheet
                            break;
                        }
                    }

                    // Safe cell access via dynamic column mapping
                    string colSnVal = colSn >= 0 ? GetSafeString(reader, colSn) : string.Empty;
                    string colCode = colItem >= 0 ? GetSafeString(reader, colItem) : string.Empty;
                    string colText = colDesc >= 0 ? GetSafeString(reader, colDesc) : string.Empty;
                    string colUnitRaw = colUnit >= 0 ? GetSafeString(reader, colUnit) : string.Empty;
                    string normUnit = NormalizeUnit(colUnitRaw);
                    object? colQtyObj = colQty >= 0 ? GetSafeValue(reader, colQty) : null;
                    decimal qty = ParseDecimal(colQtyObj);
                    object? colRateObj = colRate >= 0 ? GetSafeValue(reader, colRate) : null;
                    object? colAmtObj = colAmt >= 0 ? GetSafeValue(reader, colAmt) : null;
                    string colAmtStr = colAmt >= 0 ? GetSafeString(reader, colAmt) : string.Empty;

                    // Filter out metadata and subtotal summary rows
                    if (IsMetadataRow(colCode, colText) ||
                        colText.StartsWith("Summary of", StringComparison.OrdinalIgnoreCase) ||
                        colText.StartsWith("Total of", StringComparison.OrdinalIgnoreCase) ||
                        colText.StartsWith("إجمالي ", StringComparison.OrdinalIgnoreCase) ||
                        colText.StartsWith("Grand Total", StringComparison.OrdinalIgnoreCase) ||
                        colText.Equals("VAT", StringComparison.OrdinalIgnoreCase) ||
                        colText.StartsWith("VAT ", StringComparison.OrdinalIgnoreCase) ||
                        colText.Contains("Excluding VAT", StringComparison.OrdinalIgnoreCase) ||
                        colText.Contains("Including VAT", StringComparison.OrdinalIgnoreCase) ||
                        colText.Contains("ضريبة", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isRateOnly = colAmtStr.Contains("Rate only", StringComparison.OrdinalIgnoreCase) ||
                                      colUnitRaw.Contains("Rate only", StringComparison.OrdinalIgnoreCase);

                    // Track serial number
                    if (!string.IsNullOrWhiteSpace(colSnVal) && !colSnVal.Equals("SN", StringComparison.OrdinalIgnoreCase) && !colSnVal.Equals("م", StringComparison.OrdinalIgnoreCase) && colSnVal.Length <= 10)
                    {
                        currentSerialNumber = colSnVal;
                    }

                    // Section Detection: Only when no item code, no unit, and no qty
                    if (string.IsNullOrWhiteSpace(colCode) && qty == 0m && string.IsNullOrWhiteSpace(normUnit))
                    {
                        if (colText.StartsWith("SECTION", StringComparison.OrdinalIgnoreCase) || 
                            colText.StartsWith("BILL NO", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("PART ", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("CLASS ", StringComparison.OrdinalIgnoreCase) ||
                            colText.StartsWith("DIVISION", StringComparison.OrdinalIgnoreCase) ||
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

                    // Item Code boundary detected in Col B / Col A
                    if (!string.IsNullOrWhiteSpace(colCode) && colCode.Length <= 12 && !colCode.Equals("ITEM", StringComparison.OrdinalIgnoreCase) && !colCode.Equals("م", StringComparison.OrdinalIgnoreCase) && !colCode.Equals("بند", StringComparison.OrdinalIgnoreCase))
                    {
                        currentItemCode = colCode;
                        descBuilder.Clear();
                        startRowIndex = rowIndex;
                    }
                    else if (string.IsNullOrWhiteSpace(currentItemCode) && !string.IsNullOrWhiteSpace(currentSerialNumber))
                    {
                        currentItemCode = currentSerialNumber;
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
                    // 4. Has Quantity AND non-empty ItemCode/SerialNumber (e.g. Lump Sum items)
                    bool hasValidUnit = !string.IsNullOrWhiteSpace(normUnit) && !normUnit.Equals("unit", StringComparison.OrdinalIgnoreCase) && !normUnit.Equals("الوحدة", StringComparison.OrdinalIgnoreCase);
                    bool isAnchorRow = (qty > 0m && hasValidUnit) || 
                                       (hasValidUnit && !string.IsNullOrWhiteSpace(currentItemCode)) ||
                                       (hasValidUnit && isRateOnly) ||
                                       (qty > 0m && (!string.IsNullOrWhiteSpace(currentItemCode) || !string.IsNullOrWhiteSpace(currentSerialNumber)));

                    if (isAnchorRow)
                    {
                        decimal? rate = null;
                        decimal parsedRate = ParseDecimal(colRateObj);
                        decimal parsedAmt = ParseDecimal(colAmtObj);

                        bool isIgnoredScope = (colRateObj?.ToString()?.Contains("Ignored", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                              colAmtStr.Contains("Ignored", StringComparison.OrdinalIgnoreCase) ||
                                              (colQtyObj?.ToString()?.Contains("Ignored", StringComparison.OrdinalIgnoreCase) ?? false);

                        if (parsedRate > 0m)
                        {
                            rate = parsedRate;
                        }
                        else if (colRateObj != null && !string.IsNullOrWhiteSpace(colRateObj.ToString()) && 
                                 (colRateObj is double || colRateObj is decimal || colRateObj is int || colRateObj is long || 
                                  colRateObj.ToString()!.Trim() == "0" || colRateObj.ToString()!.Trim() == "0.0" || colRateObj.ToString()!.Trim() == "0.00"))
                        {
                            rate = 0m;
                        }
                        else if (parsedAmt > 0m && qty > 0m)
                        {
                            rate = parsedAmt / qty;
                        }
                        else if (colAmtObj != null && !string.IsNullOrWhiteSpace(colAmtStr) && 
                                 (colAmtObj is double || colAmtObj is decimal || colAmtObj is int || colAmtObj is long || 
                                  colAmtStr == "0" || colAmtStr == "0.0" || colAmtStr == "0.00"))
                        {
                            rate = 0m;
                        }
                        else if (isIgnoredScope)
                        {
                            rate = 0m;
                        }

                        string lineItemText = CompactStringPool.Shared.GetOrAdd(colText.Trim());
                        string fullDescription = CompactStringPool.Shared.GetOrAdd(descBuilder.ToString().Trim());
                        if (string.IsNullOrWhiteSpace(fullDescription))
                        {
                            fullDescription = lineItemText;
                        }

                        bool isItemPs = isPurePsSchedule ||
                                        fullDescription.Contains("provisional sum", StringComparison.OrdinalIgnoreCase) ||
                                        fullDescription.Contains("مبلغ احتياطي", StringComparison.OrdinalIgnoreCase) ||
                                        fullDescription.Contains("مبالغ احتياطية", StringComparison.OrdinalIgnoreCase);

                        var itemType = isItemPs 
                            ? BoqItemType.ProvisionalSum 
                            : (isRateOnly || (qty == 0m && !isIgnoredScope) ? BoqItemType.RateOnly : BoqItemType.Normal);

                        string finalSerialNumber = !string.IsNullOrWhiteSpace(currentSerialNumber) ? currentSerialNumber : currentItemCode;

                        var item = new BoqItem
                        {
                            Id = $"B_{sheetName}_{rowIndex}_{currentItemCode}",
                            BillNumber = sheetName,
                            SectionName = currentSection,
                            HierarchyPath = $"{Path.GetFileNameWithoutExtension(filePath)} / {sheetName}",
                            ItemCode = currentItemCode,
                            SerialNumber = finalSerialNumber,
                            LineItemText = lineItemText,
                            Description = fullDescription,
                            NormalizedDescription = NormalizeDescription(fullDescription),
                            Unit = normUnit,
                            Quantity = qty,
                            UnitRate = rate,
                            OriginalRate = rate ?? (isIgnoredScope ? 0m : null),
                            TotalAmount = rate.HasValue ? rate.Value * qty : (parsedAmt > 0m ? parsedAmt : (colAmtObj != null ? 0m : null)),
                            NumberOff = 1,
                            Currency = detectedCurrency,
                            Type = itemType,
                            SheetName = sheetName,
                            WorkbookName = Path.GetFileName(filePath),
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
                        currentSerialNumber = string.Empty;
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
