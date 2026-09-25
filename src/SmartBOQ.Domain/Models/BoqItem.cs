using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.ValueObjects;

namespace SmartBOQ.Domain.Models;

/// <summary>
/// Bill of Quantities line item entity (Immutable Domain Record).
/// </summary>
public sealed record BoqItem
{
    public required string Id { get; init; }
    public required string BillNumber { get; init; }
    public string SectionName { get; init; } = string.Empty;
    public string ItemCode { get; init; } = string.Empty;
    public required string Description { get; init; }
    public string NormalizedDescription { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal? UnitRate { get; init; }
    public decimal? TotalAmount { get; init; }
    public int NumberOff { get; init; } = 1;
    public string Currency { get; init; } = "EGP";
    public BoqItemType Type { get; init; } = BoqItemType.Normal;

    /// <summary>
    /// Physical sheet name in the Excel workbook (e.g. "Sheet1" in File A, or "Bill 02A-3BR Villa East" in File B).
    /// </summary>
    public string SheetName { get; init; } = string.Empty;

    /// <summary>
    /// 1-based Excel row index in the consultant sheet where the rate cell and formula reside.
    /// </summary>
    public int AnchorRowIndex { get; init; }

    /// <summary>
    /// 1-based column index where unit rate should be injected (defaults to 7 for Column G).
    /// </summary>
    public int RateColumnIndex { get; init; } = 7;

    /// <summary>
    /// 1-based column index where measured quantity resides (defaults to 5 for Column E).
    /// </summary>
    public int QuantityColumnIndex { get; init; } = 5;

    /// <summary>
    /// 1-based column index where computed amount resides (defaults to 8 for Column H).
    /// </summary>
    public int AmountColumnIndex { get; init; } = 8;

    /// <summary>
    /// 1-based column index where the table row begins (e.g. 3 for Column C in contractor flat BOQ, or 1 for Column A in consultant BOQ).
    /// </summary>
    public int TableStartColumnIndex { get; init; } = 1;

    /// <summary>
    /// 1-based column index where the table row ends (e.g. 19 for Column S in contractor flat BOQ, or 8 for Column H in consultant BOQ).
    /// </summary>
    public int TableEndColumnIndex { get; init; } = 8;

    /// <summary>
    /// Starting row index in the multi-row broken text block.
    /// </summary>
    public int StartRowIndex { get; init; }

    /// <summary>
    /// Indicates whether the item currently has a valid positive rate.
    /// </summary>
    public bool IsPriced => UnitRate.HasValue && UnitRate.Value > 0m;

    /// <summary>
    /// Indicates whether the item is shielded from overwrite (e.g. Provisional Sums).
    /// </summary>
    public bool IsProtected => Type == BoqItemType.ProvisionalSum;

    /// <summary>
    /// Computes total scope valuation including the model repetition multiplier (NumberOff).
    /// </summary>
    public CurrencyAmount CalculateTotalScopeAmount()
    {
        if (!UnitRate.HasValue) return new CurrencyAmount(0m, Currency);
        decimal total = UnitRate.Value * Quantity * NumberOff;
        return new CurrencyAmount(total, Currency);
    }
}
