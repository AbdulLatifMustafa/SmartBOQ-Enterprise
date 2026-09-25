using ExcelDataReader;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;

namespace SmartBOQ.Infrastructure.Parsers;

/// <summary>
/// Forward-only streaming reader for contractor flat tabular pricing schedules (e.g. DP3 - Hatchway.xlsx).
/// Inherits from <see cref="BaseBoqReader"/> and operates with constant O(1) memory overhead.
/// </summary>
public sealed class HatchwayFlatReader : BaseBoqReader
{
    public override async Task<IReadOnlyList<BoqItem>> ReadContractorFlatBoqAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Contractor pricing file not found.", filePath);
        }

        return await Task.Run(() =>
        {
            var items = new List<BoqItem>(2200);

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            int rowIndex = 0;
            string physicalSheetName = "Sheet1";
            int colRate = 17; // Default 0-indexed column 17 (Column R)

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rowIndex++;

                if (rowIndex == 1)
                {
                    physicalSheetName = string.IsNullOrWhiteSpace(reader.Name) ? "Sheet1" : reader.Name;
                    for (int c = 0; c < reader.FieldCount; c++)
                    {
                        string headerText = reader.GetValue(c)?.ToString()?.Trim() ?? string.Empty;
                        if (headerText.Contains("Net rate", StringComparison.OrdinalIgnoreCase) || 
                            headerText.Equals("Rate", StringComparison.OrdinalIgnoreCase))
                        {
                            colRate = c;
                            break;
                        }
                    }
                    continue;
                }

                // Skip second header row if present
                if (rowIndex < 2) continue;

                string? billName = GetSafeString(reader, 2);
                if (string.IsNullOrWhiteSpace(billName) || !billName.StartsWith("Bill", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string section = GetSafeString(reader, 3);
                string subSection = GetSafeString(reader, 4);
                string fullSection = string.IsNullOrWhiteSpace(subSection) ? section : CompactStringPool.Shared.GetOrAdd($"{section} > {subSection}");

                string itemCode = GetSafeString(reader, 10);
                string description = GetSafeString(reader, 11);
                if (string.IsNullOrWhiteSpace(description)) continue;

                string unit = GetSafeString(reader, 13);
                decimal qty = ParseDecimal(GetSafeValue(reader, 14));
                int numberOff = (int)Math.Round(ParseDecimal(GetSafeValue(reader, 16)));
                if (numberOff <= 0) numberOff = 1;

                decimal rate = ParseDecimal(GetSafeValue(reader, colRate));
                decimal total = ParseDecimal(GetSafeValue(reader, 18));
                string note = GetSafeString(reader, 19);

                var itemType = note.Contains("Rate only", StringComparison.OrdinalIgnoreCase) 
                    ? BoqItemType.RateOnly 
                    : BoqItemType.Normal;

                var item = new BoqItem
                {
                    Id = $"A_{rowIndex}_{itemCode}",
                    BillNumber = billName,
                    SectionName = fullSection,
                    ItemCode = itemCode,
                    Description = description,
                    NormalizedDescription = NormalizeDescription(description),
                    Unit = NormalizeUnit(unit),
                    Quantity = qty,
                    UnitRate = rate > 0m ? rate : null,
                    TotalAmount = total > 0m ? total : null,
                    NumberOff = numberOff,
                    Currency = "EGP",
                    Type = itemType,
                    SheetName = physicalSheetName,
                    StartRowIndex = rowIndex,
                    AnchorRowIndex = rowIndex,
                    RateColumnIndex = colRate + 1,
                    QuantityColumnIndex = 15, // Column O (Bill quantity)
                    AmountColumnIndex = 19,   // Column S (Net Bill amount x No off)
                    TableStartColumnIndex = 3, // Column C (Bill name through Sub-Section)
                    TableEndColumnIndex = 19   // Column S (Net Bill amount x No off)
                };

                items.Add(item);
            }

            return (IReadOnlyList<BoqItem>)items;
        }, ct);
    }
}
