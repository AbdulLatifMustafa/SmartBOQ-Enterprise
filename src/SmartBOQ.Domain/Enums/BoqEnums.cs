namespace SmartBOQ.Domain.Enums;

/// <summary>
/// Bill of Quantities item classification based on contractual and civil engineering standards.
/// </summary>
public enum BoqItemType
{
    /// <summary>Standard measured work item with quantity and unit rate.</summary>
    Normal = 0,

    /// <summary>Rate only item without fixed measured volume.</summary>
    RateOnly = 1,

    /// <summary>Provisional sum allocated by the Employer / Client (Fixed Lump Sum, shielded from injection).</summary>
    ProvisionalSum = 2,

    /// <summary>New scope item not present in the original tender schedule (Proposed Variation Order).</summary>
    VariationOrder = 3
}

/// <summary>
/// Item matching confidence level between source and target schedules.
/// </summary>
public enum MatchConfidence
{
    /// <summary>Exact deterministic match (100%).</summary>
    Exact = 0,

    /// <summary>High-confidence fuzzy textual match (>= 88%).</summary>
    HighFuzzy = 1,

    /// <summary>Borderline match requiring technical review (70% - 87%).</summary>
    ManualReviewNeeded = 2,

    /// <summary>Completely unmatched item.</summary>
    Unmatched = 3
}

/// <summary>
/// Result status from the pre-flight schema and nomenclature integrity verification gate.
/// </summary>
public enum VerificationStatus
{
    Passed = 0,
    FailedMissingColumns = 1,
    FailedInvalidSheets = 2,
    FailedCorrupted = 3
}
