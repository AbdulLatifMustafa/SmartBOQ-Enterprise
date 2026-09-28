# SmartBOQ Domain Models & Data Structures

This document provides a comprehensive reference for the core domain entities, value objects, and enums defined in `SmartBOQ.Domain`.

---

## 1. Core Entities & Value Objects

### 1.1 `BoqItem` (Immutable Domain Record)
Represents a single Bill of Quantities line item.

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
    public int TableStartColumnIndex { get; init; } = 1;
    public int TableEndColumnIndex { get; init; } = 8;

    // Computed Evaluation
    public bool IsPriced => UnitRate.HasValue && UnitRate.Value > 0m;
    public bool IsProtected => Type == BoqItemType.ProvisionalSum;

    public CurrencyAmount CalculateTotalScopeAmount();
}
```

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

### 1.3 `CurrencyBucketSummary`
Segregated financial container for a single currency without exchange rate blending.

```csharp
public sealed record CurrencyBucketSummary
{
    public required string Currency { get; init; }
    public decimal TotalBaseAmount { get; init; }
    public decimal TotalRemeasureAmount { get; init; }
    public decimal VarianceAmount => TotalRemeasureAmount - TotalBaseAmount;
    public double VariancePercentage => TotalBaseAmount == 0m ? 0.0 : (double)(VarianceAmount / TotalBaseAmount) * 100.0;
    public int ItemsCount { get; init; }
}
```

### 1.4 `HistoricalRateItem`
Rate benchmarking record stored in the local SQLite repository for price history comparisons.

```csharp
public sealed record HistoricalRateItem
{
    public long RevisionId { get; init; }
    public string InvoiceName { get; init; } = string.Empty;
    public string ProjectCode { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public string TargetFileName { get; init; } = string.Empty;
    public string ExportFilePath { get; init; } = string.Empty;
    public DateTime SnapshotDate { get; init; }
    public string BillNumber { get; init; } = string.Empty;
    public string ItemCode { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal? UnitRate { get; init; }
    public decimal? TotalAmount { get; init; }
    public string Currency { get; init; } = "EGP";
}
```

---

## 2. Enumerations

### 2.1 `MatchConfidence`
* `Exact`: 100% algorithmic certainty via identical item code or exact normalized description hash.
* `HighFuzzy`: Similarity score $\ge 0.85$. High confidence fuzzy vector match.
* `ManualReviewNeeded`: Similarity score between $0.70$ and $0.84$. Flagged for engineering review.
* `Unmatched`: Score $< 0.70$. Classified as Variation Order (VO) or unpriced item.

### 2.2 `BoqItemType`
* `Normal`: Standard bill line item to be priced.
* `ProvisionalSum`: Contractually shielded item. Protected from contractor rate overwrite.
* `RateOnly`: Quantities are zero or unmeasured; only the unit rate applies.
* `VariationOrder`: Newly added unpriced scope.

### 2.3 `BoqFileRole`
* `ContractorPriced`: Pricing source schedule containing master unit rates.
* `ConsultantTarget`: Contractual tender schedule into which rates will be reconciled and injected.

### 2.4 `VerificationStatus`
* `Passed`: Pre-flight verification passed all checks.
* `Warning`: Minor non-critical schema variances detected.
* `Failed`: Critical corruption, missing required columns, or invalid workbook structure.
