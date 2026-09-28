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
            using var reader = ExcelReaderFactory.CreateReader(stream);

            // Step 1: Detect column schema dynamically or apply user/active routed channels
            ResolvedBoqColumns resolvedCols = SemanticColumnResolver.ResolveColumns(reader, maxScanRows: 35);
            if (columnMappings != null)
            {
                resolvedCols = resolvedCols with
                {
                    RateColumn = columnMappings.SourceRateColumn >= 0 ? columnMappings.SourceRateColumn : resolvedCols.RateColumn,
                    DescriptionColumn = columnMappings.SourceDescColumn >= 0 ? columnMappings.SourceDescColumn : resolvedCols.DescriptionColumn,
                    ItemCodeColumn = columnMappings.SourceCodeColumn >= 0 ? columnMappings.SourceCodeColumn : resolvedCols.ItemCodeColumn,
                    QuantityColumn = columnMappings.SourceQtyColumn >= 0 ? columnMappings.SourceQtyColumn : resolvedCols.QuantityColumn,
                    UnitColumn = columnMappings.SourceUnitColumn >= 0 ? columnMappings.SourceUnitColumn : resolvedCols.UnitColumn
                };
            }

            // Reset stream to re-read data rows from beginning
            stream.Seek(0, SeekOrigin.Begin);
            using var dataReader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                ct.ThrowIfCancellationRequested();
                string sheetName = string.IsNullOrWhiteSpace(dataReader.Name) ? "Sheet1" : dataReader.Name.Trim();
                if (IsNonBillSheet(sheetName)) continue;

                int rowIndex = 0;
                string currentBillName = sheetName;
                string currentSection = string.Empty;
                BoqItem? lastItem = null;

                while (dataReader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rowIndex++;

                    // Skip pre-header and header rows
                    if (rowIndex <= resolvedCols.HeaderRowIndex)
                    {
                        continue;
                    }

                // Extract Description
                string description = resolvedCols.DescriptionColumn >= 0 
                    ? GetSafeString(dataReader, resolvedCols.DescriptionColumn) 
                    : string.Empty;

                // Extract Bill identifier dynamically if available
                if (resolvedCols.BillColumn >= 0)
                {
                    string rawBill = GetSafeString(dataReader, resolvedCols.BillColumn);
                    if (!string.IsNullOrWhiteSpace(rawBill))
                    {
                        currentBillName = rawBill;
                    }
                }

                // Extract Section identifier dynamically if available
                if (resolvedCols.SectionColumn >= 0)
                {
                    string rawSec = GetSafeString(dataReader, resolvedCols.SectionColumn);
                    if (!string.IsNullOrWhiteSpace(rawSec))
                    {
                        currentSection = rawSec;
                    }
                }

                // Extract core numeric and code values
                string itemCode = resolvedCols.ItemCodeColumn >= 0 ? GetSafeString(dataReader, resolvedCols.ItemCodeColumn) : string.Empty;
                string rawUnit = resolvedCols.UnitColumn >= 0 ? GetSafeString(dataReader, resolvedCols.UnitColumn) : string.Empty;
                string unit = NormalizeUnit(rawUnit);
                decimal qty = resolvedCols.QuantityColumn >= 0 ? ParseDecimal(GetSafeValue(dataReader, resolvedCols.QuantityColumn)) : 0m;
                decimal rate = resolvedCols.RateColumn >= 0 ? ParseDecimal(GetSafeValue(dataReader, resolvedCols.RateColumn)) : 0m;
                decimal total = resolvedCols.TotalAmountColumn >= 0 ? ParseDecimal(GetSafeValue(dataReader, resolvedCols.TotalAmountColumn)) : 0m;
                
                int numberOff = resolvedCols.NumberOffColumn >= 0 
                    ? (int)Math.Round(ParseDecimal(GetSafeValue(dataReader, resolvedCols.NumberOffColumn))) 
                    : 1;
                if (numberOff <= 0) numberOff = 1;

                string note = resolvedCols.NoteColumn >= 0 ? GetSafeString(dataReader, resolvedCols.NoteColumn) : string.Empty;

                // Skip header repetitions or empty rows
                if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(itemCode) && rate == 0m && qty == 0m)
                {
                    continue;
                }

                if (description.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
                    description.Equals("الوصف", StringComparison.OrdinalIgnoreCase) ||
                    description.Equals("البيان", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Continuation row handling (multi-line broken descriptions)
                if (string.IsNullOrWhiteSpace(itemCode) && qty == 0m && rate == 0m && total == 0m && !string.IsNullOrWhiteSpace(description))
                {
                    if (lastItem != null && items.Count > 0)
                    {
                        // Append continuation line to previous item's description
                        int lastIdx = items.Count - 1;
                        var prev = items[lastIdx];
                        string mergedDesc = CompactStringPool.Shared.GetOrAdd($"{prev.Description} {description}");
                        items[lastIdx] = prev with
                        {
                            Description = mergedDesc,
                            NormalizedDescription = NormalizeDescription(mergedDesc)
                        };
                        lastItem = items[lastIdx];
                    }
                    continue;
                }

                if (total == 0m && rate > 0m && qty > 0m)
                {
                    total = rate * qty * numberOff;
                }

                // Determine Item Type
                var itemType = BoqItemType.Normal;
                if (note.Contains("Rate only", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("سعر فقط", StringComparison.OrdinalIgnoreCase) ||
                    (qty == 0m && rate > 0m))
                {
                    itemType = BoqItemType.RateOnly;
                }
                else if (description.Contains("provisional sum", StringComparison.OrdinalIgnoreCase) ||
                         description.Contains("مبلغ احتياطي", StringComparison.OrdinalIgnoreCase) ||
                         description.Contains("مقطوعية", StringComparison.OrdinalIgnoreCase) && rate == 0m)
                {
                    itemType = BoqItemType.ProvisionalSum;
                }

                int tblStart = Math.Max(1, Math.Min(resolvedCols.BillColumn >= 0 ? resolvedCols.BillColumn : 0, resolvedCols.DescriptionColumn) + 1);
                int tblEnd = Math.Max(resolvedCols.RateColumn >= 0 ? resolvedCols.RateColumn : 10, resolvedCols.TotalAmountColumn >= 0 ? resolvedCols.TotalAmountColumn : 12) + 1;

                var item = new BoqItem
                {
                    Id = $"U_{sheetName}_{rowIndex}_{itemCode}",
                    BillNumber = currentBillName,
                    SectionName = currentSection,
                    ItemCode = itemCode,
                    Description = description,
                    NormalizedDescription = NormalizeDescription(description),
                    Unit = NormalizeUnit(unit),
                    Quantity = qty,
                    UnitRate = rate > 0m ? rate : null,
                    TotalAmount = total > 0m ? total : null,
                    NumberOff = numberOff,
                    Currency = resolvedCols.DetectedCurrency,
                    Type = itemType,
                    SheetName = sheetName,
                    StartRowIndex = rowIndex,
                    AnchorRowIndex = rowIndex,
                    RateColumnIndex = resolvedCols.RateColumn >= 0 ? resolvedCols.RateColumn + 1 : 0,
                    QuantityColumnIndex = resolvedCols.QuantityColumn >= 0 ? resolvedCols.QuantityColumn + 1 : 0,
                    AmountColumnIndex = resolvedCols.TotalAmountColumn >= 0 ? resolvedCols.TotalAmountColumn + 1 : 0,
                    TableStartColumnIndex = tblStart,
                    TableEndColumnIndex = tblEnd
                };

                items.Add(item);
                lastItem = item;
            }
        } while (dataReader.NextResult());

        return (IReadOnlyList<BoqItem>)items;
        }, ct);
    }
}
