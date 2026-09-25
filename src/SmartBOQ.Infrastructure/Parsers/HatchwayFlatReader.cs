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
            string detectedCurrency = "EGP";

            // Default column mapping for standard contractor flat format (Hatchway DP3 fallback)
            int colBill = 2;
            int colSection = 3;
            int colSubSection = 4;
            int colItemCode = 10;
            int colDesc = 11;
            int colUnit = 13;
            int colQty = 14;
            int colNumberOff = 16;
            int colRate = 17; // Default 0-indexed column 17 (Column R)
            int colTotal = 18;
            int colNote = 19;

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
                        if (string.IsNullOrWhiteSpace(headerText)) continue;

                        if (headerText.Contains("USD", StringComparison.OrdinalIgnoreCase) || headerText.Contains("($)")) detectedCurrency = "USD";
                        else if (headerText.Contains("EUR", StringComparison.OrdinalIgnoreCase) || headerText.Contains("(€)")) detectedCurrency = "EUR";
                    }
                    continue;
                }

                // Skip header rows
                if (rowIndex < 2) continue;

                string? billName = GetSafeString(reader, colBill);
                if (string.IsNullOrWhiteSpace(billName) || !billName.StartsWith("Bill", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string section = colSection >= 0 ? GetSafeString(reader, colSection) : string.Empty;
                string subSection = colSubSection >= 0 ? GetSafeString(reader, colSubSection) : string.Empty;
                string fullSection = string.IsNullOrWhiteSpace(subSection) ? section : CompactStringPool.Shared.GetOrAdd($"{section} > {subSection}");

                string itemCode = colItemCode >= 0 ? GetSafeString(reader, colItemCode) : string.Empty;
                string description = GetSafeString(reader, colDesc);
                if (string.IsNullOrWhiteSpace(description) || description.Equals("Description", StringComparison.OrdinalIgnoreCase)) continue;

                string unit = colUnit >= 0 ? GetSafeString(reader, colUnit) : string.Empty;
                decimal qty = colQty >= 0 ? ParseDecimal(GetSafeValue(reader, colQty)) : 0m;
                int numberOff = colNumberOff >= 0 ? (int)Math.Round(ParseDecimal(GetSafeValue(reader, colNumberOff))) : 1;
                if (numberOff <= 0) numberOff = 1;

                decimal rate = colRate >= 0 ? ParseDecimal(GetSafeValue(reader, colRate)) : 0m;
                decimal total = colTotal >= 0 ? ParseDecimal(GetSafeValue(reader, colTotal)) : (rate * qty * numberOff);
                string note = colNote >= 0 ? GetSafeString(reader, colNote) : string.Empty;

                var itemType = note.Contains("Rate only", StringComparison.OrdinalIgnoreCase) || (qty == 0m && rate > 0m)
                    ? BoqItemType.RateOnly 
                    : BoqItemType.Normal;

                int tblStart = Math.Min(colBill, Math.Min(colItemCode >= 0 ? colItemCode : colDesc, colDesc)) + 1;
                int tblEnd = Math.Max(colRate >= 0 ? colRate : 0, colTotal >= 0 ? colTotal : 0) + 1;
                if (tblEnd <= tblStart) tblEnd = tblStart + 16;

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
                    Currency = detectedCurrency,
                    Type = itemType,
                    SheetName = physicalSheetName,
                    StartRowIndex = rowIndex,
                    AnchorRowIndex = rowIndex,
                    RateColumnIndex = colRate >= 0 ? colRate + 1 : 18,
                    QuantityColumnIndex = colQty >= 0 ? colQty + 1 : 15,
                    AmountColumnIndex = colTotal >= 0 ? colTotal + 1 : 19,
                    TableStartColumnIndex = tblStart,
                    TableEndColumnIndex = tblEnd
                };

                items.Add(item);
            }

            return (IReadOnlyList<BoqItem>)items;
        }, ct);
    }
}
