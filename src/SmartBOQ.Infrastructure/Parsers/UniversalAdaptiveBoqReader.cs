using ExcelDataReader;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;

namespace SmartBOQ.Infrastructure.Parsers;

/// <summary>
/// Universal Adaptive BOQ Streaming Reader.
/// Dynamically reads contractor and consultant tabular BOQs of ANY format, language (Arabic/English),
/// column order, and size without any hardcoded column positions or file-specific rules.
/// </summary>
public class UniversalAdaptiveBoqReader : BaseBoqReader
{
    public override Task<IReadOnlyList<BoqItem>> ReadContractorFlatBoqAsync(string filePath, CancellationToken ct = default)
    {
        return ReadContractorFlatBoqAsync(filePath, null, ct);
    }

    public override async Task<IReadOnlyList<BoqItem>> ReadContractorFlatBoqAsync(
        string filePath,
        ColumnMappingModel? columnMappings,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("BOQ file not found.", filePath);
        }

        return await Task.Run(() =>
        {
            var items = new List<BoqItem>(4096);

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var dataReader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                ct.ThrowIfCancellationRequested();
                string sheetName = string.IsNullOrWhiteSpace(dataReader.Name) ? "Sheet1" : dataReader.Name.Trim();
                if (IsNonBillSheet(sheetName)) continue;

                // Smart Pre-Scan: Buffer the first 40 candidate rows of THIS sheet to determine schema & viability
                var buffer = new List<string[]>(40);
                var rawBuffer = new List<object?[]>(40);
                int rowsBuffered = 0;

                while (rowsBuffered < 40 && dataReader.Read())
                {
                    rowsBuffered++;
                    int fieldCount = dataReader.FieldCount;
                    var strVals = new string[fieldCount];
                    var rawVals = new object?[fieldCount];
                    for (int c = 0; c < fieldCount; c++)
                    {
                        rawVals[c] = dataReader.GetValue(c);
                        strVals[c] = rawVals[c]?.ToString()?.Trim() ?? string.Empty;
                    }
                    buffer.Add(strVals);
                    rawBuffer.Add(rawVals);
                }

                if (buffer.Count == 0) continue;

                // Check if this sheet is a Pivot Table or administrative summary sheet
                if (SemanticColumnResolver.IsPivotOrSummarySheet(buffer))
                {
                    continue;
                }

                // Resolve column mapping dynamically for THIS SPECIFIC SHEET
                ResolvedBoqColumns resolvedCols = SemanticColumnResolver.ResolveColumnsFromRows(buffer, dataReader.FieldCount);

                // Apply explicit user mappings if provided by user (manual override) or as fallback if detection missed a column
                if (columnMappings != null)
                {
                    bool isManualOverride = !columnMappings.IsAutoDetected;
                    resolvedCols = resolvedCols with
                    {
                        RateColumn = isManualOverride && columnMappings.SourceRateColumn >= 0 ? columnMappings.SourceRateColumn : (resolvedCols.RateColumn >= 0 ? resolvedCols.RateColumn : columnMappings.SourceRateColumn),
                        DescriptionColumn = isManualOverride && columnMappings.SourceDescColumn >= 0 ? columnMappings.SourceDescColumn : (resolvedCols.DescriptionColumn >= 0 ? resolvedCols.DescriptionColumn : columnMappings.SourceDescColumn),
                        ItemCodeColumn = isManualOverride && columnMappings.SourceCodeColumn >= 0 ? columnMappings.SourceCodeColumn : (resolvedCols.ItemCodeColumn >= 0 ? resolvedCols.ItemCodeColumn : columnMappings.SourceCodeColumn),
                        QuantityColumn = isManualOverride && columnMappings.SourceQtyColumn >= 0 ? columnMappings.SourceQtyColumn : (resolvedCols.QuantityColumn >= 0 ? resolvedCols.QuantityColumn : columnMappings.SourceQtyColumn),
                        UnitColumn = isManualOverride && columnMappings.SourceUnitColumn >= 0 ? columnMappings.SourceUnitColumn : (resolvedCols.UnitColumn >= 0 ? resolvedCols.UnitColumn : columnMappings.SourceUnitColumn)
                    };
                }

                int rowIndex = 0;
                string currentBillName = sheetName;
                string currentSection = string.Empty;
                var pendingContext = new List<string>(8);
                BoqItem? lastItem = null;

                void ProcessRow(int rIdx, Func<int, string> getStr, Func<int, object?> getVal)
                {
                    if (rIdx <= resolvedCols.HeaderRowIndex) return;

                    string description = resolvedCols.DescriptionColumn >= 0 
                        ? getStr(resolvedCols.DescriptionColumn) 
                        : string.Empty;

                    // Extract multi-column hierarchy path if available
                    string hierarchyPath = string.Empty;
                    if (resolvedCols.HierarchyColumns.Count > 0)
                    {
                        var levels = new List<string>(resolvedCols.HierarchyColumns.Count);
                        for (int h = 0; h < resolvedCols.HierarchyColumns.Count; h++)
                        {
                            string lvl = getStr(resolvedCols.HierarchyColumns[h]);
                            if (!string.IsNullOrWhiteSpace(lvl) && !levels.Contains(lvl, StringComparer.OrdinalIgnoreCase))
                            {
                                levels.Add(lvl);
                            }
                        }
                        if (levels.Count > 0)
                        {
                            hierarchyPath = string.Join(" / ", levels);
                            if (levels.Count >= 2)
                            {
                                currentBillName = levels[^1];
                                currentSection = levels.Count >= 3 ? levels[^2] : levels[0];
                            }
                            else
                            {
                                currentBillName = levels[0];
                            }
                        }
                    }

                    if (resolvedCols.BillColumn >= 0)
                    {
                        string rawBill = getStr(resolvedCols.BillColumn);
                        if (!string.IsNullOrWhiteSpace(rawBill)) currentBillName = rawBill;
                    }

                    if (resolvedCols.SectionColumn >= 0)
                    {
                        string rawSec = getStr(resolvedCols.SectionColumn);
                        if (!string.IsNullOrWhiteSpace(rawSec)) currentSection = rawSec;
                    }

                    string itemCode = resolvedCols.ItemCodeColumn >= 0 ? getStr(resolvedCols.ItemCodeColumn) : string.Empty;
                    string serialNumber = resolvedCols.SerialNumberColumn >= 0 ? getStr(resolvedCols.SerialNumberColumn) : string.Empty;
                    if (string.IsNullOrWhiteSpace(itemCode) && !string.IsNullOrWhiteSpace(serialNumber))
                    {
                        itemCode = serialNumber;
                    }

                    string rawUnit = resolvedCols.UnitColumn >= 0 ? getStr(resolvedCols.UnitColumn) : string.Empty;
                    string unit = NormalizeUnit(rawUnit);
                    decimal qty = resolvedCols.QuantityColumn >= 0 ? ParseDecimal(getVal(resolvedCols.QuantityColumn)) : 0m;
                    decimal rate = resolvedCols.RateColumn >= 0 ? ParseDecimal(getVal(resolvedCols.RateColumn)) : 0m;
                    decimal total = resolvedCols.TotalAmountColumn >= 0 ? ParseDecimal(getVal(resolvedCols.TotalAmountColumn)) : 0m;

                    int numberOff = resolvedCols.NumberOffColumn >= 0 
                        ? (int)Math.Round(ParseDecimal(getVal(resolvedCols.NumberOffColumn))) 
                        : 1;
                    if (numberOff <= 0) numberOff = 1;

                    string note = resolvedCols.NoteColumn >= 0 ? getStr(resolvedCols.NoteColumn) : string.Empty;

                    if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(itemCode) && rate == 0m && qty == 0m)
                    {
                        return;
                    }

                    if (description.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
                        description.Equals("الوصف", StringComparison.OrdinalIgnoreCase) ||
                        description.Equals("البيان", StringComparison.OrdinalIgnoreCase) ||
                        description.Equals("Bill Item Description", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (IsMetadataRow(itemCode, description))
                    {
                        return;
                    }

                    // Context accumulation: If row has descriptive text but no qty and no rate
                    if (string.IsNullOrWhiteSpace(itemCode) && qty == 0m && rate == 0m && total == 0m && !string.IsNullOrWhiteSpace(description))
                    {
                        if (description.StartsWith("SECTION", StringComparison.OrdinalIgnoreCase) || 
                            description.StartsWith("BILL NO", StringComparison.OrdinalIgnoreCase) ||
                            description.StartsWith("PART ", StringComparison.OrdinalIgnoreCase) ||
                            description.StartsWith("DIVISION", StringComparison.OrdinalIgnoreCase) ||
                            description.StartsWith("باب", StringComparison.OrdinalIgnoreCase) ||
                            description.StartsWith("قسم", StringComparison.OrdinalIgnoreCase))
                        {
                            currentSection = description;
                            pendingContext.Clear();
                        }
                        else
                        {
                            pendingContext.Add(description);
                        }
                        return;
                    }

                    if (total == 0m && rate > 0m && qty > 0m)
                    {
                        total = rate * qty * numberOff;
                    }

                    var itemType = BoqItemType.Normal;
                    if (note.Contains("Rate only", StringComparison.OrdinalIgnoreCase) ||
                        note.Contains("سعر فقط", StringComparison.OrdinalIgnoreCase) ||
                        (qty == 0m && rate > 0m))
                    {
                        itemType = BoqItemType.RateOnly;
                    }
                    else if (description.Contains("provisional sum", StringComparison.OrdinalIgnoreCase) ||
                             description.Contains("مبلغ احتياطي", StringComparison.OrdinalIgnoreCase) ||
                             (description.Contains("مقطوعية", StringComparison.OrdinalIgnoreCase) && rate == 0m))
                    {
                        itemType = BoqItemType.ProvisionalSum;
                    }

                    string lineItemText = description;
                    string fullDescription = description;
                    if (pendingContext.Count > 0)
                    {
                        fullDescription = $"{string.Join(" ", pendingContext)} {description}".Trim();
                        pendingContext.Clear();
                    }

                    int tblStart = Math.Max(1, Math.Min(resolvedCols.BillColumn >= 0 ? resolvedCols.BillColumn : 0, resolvedCols.DescriptionColumn) + 1);
                    int tblEnd = Math.Max(resolvedCols.RateColumn >= 0 ? resolvedCols.RateColumn : 10, resolvedCols.TotalAmountColumn >= 0 ? resolvedCols.TotalAmountColumn : 12) + 1;

                    var item = new BoqItem
                    {
                        Id = $"U_{sheetName}_{rIdx}_{itemCode}",
                        BillNumber = currentBillName,
                        SectionName = currentSection,
                        HierarchyPath = hierarchyPath,
                        ItemCode = itemCode,
                        SerialNumber = serialNumber,
                        LineItemText = lineItemText,
                        Description = fullDescription,
                        NormalizedDescription = NormalizeDescription(fullDescription),
                        Unit = NormalizeUnit(unit),
                        Quantity = qty,
                        UnitRate = rate >= 0m ? rate : null,
                        OriginalRate = rate >= 0m ? rate : null,
                        TotalAmount = total >= 0m ? total : null,
                        NumberOff = numberOff,
                        Currency = resolvedCols.DetectedCurrency,
                        Type = itemType,
                        SheetName = sheetName,
                        WorkbookName = Path.GetFileName(filePath),
                        StartRowIndex = rIdx,
                        AnchorRowIndex = rIdx,
                        RateColumnIndex = resolvedCols.RateColumn >= 0 ? resolvedCols.RateColumn + 1 : 0,
                        QuantityColumnIndex = resolvedCols.QuantityColumn >= 0 ? resolvedCols.QuantityColumn + 1 : 0,
                        AmountColumnIndex = resolvedCols.TotalAmountColumn >= 0 ? resolvedCols.TotalAmountColumn + 1 : 0,
                        TableStartColumnIndex = tblStart,
                        TableEndColumnIndex = tblEnd
                    };

                    items.Add(item);
                    lastItem = item;
                }

                // 1. Process buffered rows first
                for (int b = 0; b < buffer.Count; b++)
                {
                    rowIndex++;
                    var bRowStr = buffer[b];
                    var bRowVal = rawBuffer[b];
                    ProcessRow(rowIndex, c => c < bRowStr.Length ? bRowStr[c] : string.Empty, c => c < bRowVal.Length ? bRowVal[c] : null);
                }

                // 2. Continue streaming remaining rows
                while (dataReader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rowIndex++;
                    ProcessRow(rowIndex, c => GetSafeString(dataReader, c), c => GetSafeValue(dataReader, c));
                }

            } while (dataReader.NextResult());

            return (IReadOnlyList<BoqItem>)items;
        }, ct);
    }
}
