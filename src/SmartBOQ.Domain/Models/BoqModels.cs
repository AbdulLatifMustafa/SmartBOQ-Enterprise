using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.ValueObjects;

namespace SmartBOQ.Domain.Models;

/// <summary>
/// Represents an entire worksheet in the Bill of Quantities hierarchy.
/// </summary>
public sealed record BoqSheet
{
    public required string SheetName { get; init; }
    public required string BillCode { get; init; }
    public IReadOnlyList<BoqItem> Items { get; init; } = Array.Empty<BoqItem>();
    public bool IsProvisionalSumSheet { get; init; }
    public string Currency { get; init; } = "EGP";

    public int TotalItemsCount => Items.Count;
    public int PricedItemsCount => Items.Count(i => i.IsPriced);
    public int BlankRateCount => Items.Count(i => !i.IsPriced && i.Type == BoqItemType.Normal);

    /// <summary>
    /// Computes the net sheet total strictly in its designated currency.
    /// </summary>
    public CurrencyAmount CalculateSheetTotal()
    {
        decimal sum = Items.Where(i => i.UnitRate.HasValue)
                           .Sum(i => i.UnitRate!.Value * i.Quantity);
        return new CurrencyAmount(sum, Currency);
    }
}

/// <summary>
/// Reconciliation match pair between source and target items.
/// </summary>
public sealed record BoqMatchedPair
{
    public required BoqItem TargetItem { get; init; }
    public BoqItem? MatchedSourceItem { get; init; }
    public double SimilarityScore { get; init; }
    public MatchConfidence Confidence { get; init; }
    public string MatchRationale { get; init; } = string.Empty;
    public bool IsApproved { get; set; }

    private decimal? _injectedRate;

    /// <summary>
    /// Rate proposed for injection from the verified reference file or custom overridden by engineer.
    /// </summary>
    public decimal? InjectedRate
    {
        get => _injectedRate ?? MatchedSourceItem?.UnitRate;
        set => _injectedRate = value;
    }

    /// <summary>
    /// Indicates whether this item represents an unpriced new scope / Variation Order.
    /// </summary>
    public bool IsVariationOrder => TargetItem.Type == BoqItemType.VariationOrder || 
                                   (MatchedSourceItem == null && TargetItem.Type == BoqItemType.Normal);

    /// <summary>
    /// Indicates whether this item is a contractually shielded Provisional Sum.
    /// </summary>
    public bool IsProvisionalSum => TargetItem.Type == BoqItemType.ProvisionalSum;

    /// <summary>
    /// Formatted display text for the rate in grids (shows rate or badge for PS / VO).
    /// </summary>
    public string DisplayRateText => InjectedRate.HasValue
        ? InjectedRate.Value.ToString("N2")
        : (IsProvisionalSum ? "محمي (PS)" : "بند مستحدث (VO)");

    /// <summary>
    /// Localized human-readable status for confidence in grids.
    /// </summary>
    public string DisplayConfidenceText => IsProvisionalSum
        ? "مبلغ احتياطي محمي"
        : Confidence switch
        {
            MatchConfidence.Exact => "تطابق تام (100%)",
            MatchConfidence.HighFuzzy => $"تطابق ذكي ({SimilarityScore:P0})",
            MatchConfidence.ManualReviewNeeded => "مراجعة فنية",
            _ => IsVariationOrder ? "بند مستحدث (VO)" : "غير مطابق"
        };
}

/// <summary>
/// Segregated financial container for a single currency.
/// </summary>
public sealed record CurrencyBucketSummary
{
    public required string Currency { get; init; }
    public decimal TotalBaseAmount { get; init; }
    public decimal TotalRemeasureAmount { get; init; }
    public decimal VarianceAmount => TotalRemeasureAmount - TotalBaseAmount;
    public double VariancePercentage => TotalBaseAmount == 0m ? 0.0 : (double)(VarianceAmount / TotalBaseAmount) * 100.0;
    public int ItemsCount { get; init; }
}

/// <summary>
/// Pre-flight schema and nomenclature integrity verification report.
/// </summary>
public sealed record VerificationReport
{
    public bool IsValid { get; init; }
    public VerificationStatus Status { get; init; }
    public IReadOnlyList<string> PassedChecks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public int FileARowsCount { get; init; }
    public int FileBSheetsCount { get; init; }
}

/// <summary>
/// Project revision snapshot stored in the local SQLite repository.
/// </summary>
public sealed record ProjectSnapshot
{
    public long RevisionId { get; init; }
    public required string ProjectCode { get; init; }
    public required string RevisionCode { get; init; } // e.g. "Rev 0", "Rev 1", "Rev 2"
    public DateTime SnapshotDate { get; init; } = DateTime.UtcNow;
    public decimal TotalValueEgp { get; init; }
    public decimal TotalValueUsd { get; init; }
    public int TotalItemsCount { get; init; }
    public string ContentHash { get; init; } = string.Empty;
}
