# SmartBOQ Domain Models & Data Structures

This document provides a comprehensive reference for the core domain entities, value objects, and enums defined in `SmartBOQ.Domain`.

---

## 1. Core Entities & Value Objects

### 1.1 `BoqItem` (Immutable Domain Record)
Represents a single bill of quantities line item.

```csharp
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

    // Physical Excel Layout Properties
    public string SheetName { get; init; } = string.Empty;
    public int AnchorRowIndex { get; init; }
    public int StartRowIndex { get; init; }
    public int RateColumnIndex { get; init; } = 7;     // Default Column G
    public int QuantityColumnIndex { get; init; } = 5; // Default Column E
    public int AmountColumnIndex { get; init; } = 8;   // Default Column H

    // Table Boundary Bounds for Smart Multi-Cell Selection
    public int TableStartColumnIndex { get; init; } = 1; // e.g. Col C (3) in contractor flat schedule
    public int TableEndColumnIndex { get; init; } = 8;   // e.g. Col S (19) in contractor flat schedule

    // Computed Evaluation
    public bool IsPriced => UnitRate.HasValue && UnitRate.Value > 0m;
    public bool IsProtected => Type == BoqItemType.ProvisionalSum;

    public CurrencyAmount CalculateTotalScopeAmount();
}
```

#### Key Design Decisions on `BoqItem`:
1. **Multi-Row Descriptions**: In consultant sheets (e.g. `REH.1.xlsx`), item descriptions often span 3 to 10 broken lines. `StartRowIndex` marks the first text row, while `AnchorRowIndex` represents the final row containing the quantity, rate, and amount cells.
2. **Table Start and End Indices**: `TableStartColumnIndex` and `TableEndColumnIndex` allow the exporter to dynamically calculate multi-cell ranges (`C{row}:S{row}`) without hardcoding column numbers for different spreadsheet variants.
3. **NumberOff Multiplier**: Accounts for model repetitions (e.g., 49 identical villa units in `DP3 - Hatchway.xlsx`).

---

### 1.2 `BoqMatchedPair`
Represents the matched association between a consultant target item and a contractor source item.

```csharp
public sealed record BoqMatchedPair
{
    public required BoqItem TargetItem { get; init; }
    public BoqItem? MatchedSourceItem { get; init; }
    public double SimilarityScore { get; init; }
    public MatchConfidence Confidence { get; init; }
    public string MatchRationale { get; init; } = string.Empty;
    public bool IsApproved { get; set; }

    public decimal? InjectedRate { get; set; }
    public bool IsVariationOrder => TargetItem.Type == BoqItemType.VariationOrder || 
                                   (MatchedSourceItem == null && TargetItem.Type == BoqItemType.Normal);
    public bool IsProvisionalSum => TargetItem.Type == BoqItemType.ProvisionalSum;
}
```

---

### 1.3 `CurrencyAmount` (Value Object)
Enforces mathematical safety and prevents currency blending.

```csharp
public readonly struct CurrencyAmount : IEquatable<CurrencyAmount>
{
    public decimal Value { get; }
    public string Currency { get; }

    public CurrencyAmount(decimal value, string currency);
    public static CurrencyAmount operator +(CurrencyAmount left, CurrencyAmount right);
    public static CurrencyAmount operator -(CurrencyAmount left, CurrencyAmount right);
}
```

> [!IMPORTANT]
> If an addition or subtraction is attempted between two different currencies (e.g., `EGP + USD`), `CurrencyAmount` throws an `InvalidOperationException` immediately. Currency conversion must be explicit and tracked.

---

## 2. Enumerations

### 2.1 `MatchConfidence`
Defines the certainty level of the reconciliation match:
* `Exact`: 100% algorithmic certainty. Matched via identical item codes within the same bill/section, or exact normalized description hash.
* `HighFuzzy`: Similarity score $\ge 0.85$. Minor word permutations or punctuation differences.
* `ManualReviewNeeded`: Similarity score between $0.70$ and $0.84$. Flagged for engineering approval.
* `Unmatched`: Score $< 0.70$. Classified as a new scope or Variation Order (VO).

### 2.2 `BoqItemType`
* `Normal`: Standard bill line item to be priced.
* `ProvisionalSum`: Contractually shielded item. Protected from contractor rate overwrite.
* `RateOnly`: Quantities are zero or unmeasured; only the unit rate applies.
* `VariationOrder`: Newly added unpriced scope.

### 2.3 `VerificationStatus`
* `Passed`: Pre-flight verification passed all checks.
* `Warning`: Minor non-critical schema variances detected.
* `Failed`: Critical corruption, missing required columns, or invalid workbook structure.
