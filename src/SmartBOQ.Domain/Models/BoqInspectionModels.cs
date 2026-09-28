using SmartBOQ.Domain.Enums;

namespace SmartBOQ.Domain.Models;

/// <summary>
/// Architectural and financial metadata for an ingested Excel BOQ workbook.
/// </summary>
public sealed record BoqFileInfo
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public string FileSizeFormatted => FileSizeBytes switch
    {
        >= 1024 * 1024 => $"{(double)FileSizeBytes / (1024 * 1024):F1} MB",
        >= 1024 => $"{(double)FileSizeBytes / 1024:F0} KB",
        _ => $"{FileSizeBytes} B"
    };

    public BoqFileRole Role { get; set; } = BoqFileRole.ContractorPriced;
    public int SheetsCount { get; init; }
    public int TotalEstimatedItems { get; init; }
    public string DetectedCurrency { get; init; } = "EGP";
    public IReadOnlyList<BoqSheetSummary> Sheets { get; init; } = Array.Empty<BoqSheetSummary>();
    public IReadOnlyList<string> AvailableColumns { get; init; } = Array.Empty<string>();
    public bool IsPrimary { get; set; }

    public string RoleBadgeText => Role switch
    {
        BoqFileRole.ContractorPriced => "ملف المقاول المسعر (Source)",
        BoqFileRole.ConsultantTarget => "جدول الاستشاري المستهدف (Target)",
        BoqFileRole.SupplementaryRates => "ملف أسعار إضافي / موردين (Rates)",
        _ => "ملف مرجعي"
    };

    public string RoleBadgeColor => Role switch
    {
        BoqFileRole.ContractorPriced => "#CCD0D9",
        BoqFileRole.ConsultantTarget => "#CCD0D9",
        BoqFileRole.SupplementaryRates => "#D4954A",
        _ => "#888E9B"
    };

    public string RoleBadgeBackground => Role switch
    {
        BoqFileRole.ContractorPriced => "#282C37",
        BoqFileRole.ConsultantTarget => "#252B35",
        BoqFileRole.SupplementaryRates => "#2D261E",
        _ => "#20222A"
    };
}

/// <summary>
/// Summary metadata for an individual worksheet or detected data table within a workbook.
/// </summary>
public sealed record BoqSheetSummary
{
    public required string SheetName { get; init; }
    public required string BillCode { get; init; }
    public int EstimatedRows { get; init; }
    public int StartRowIndex { get; init; } = 1;
    public bool IsProvisionalSum { get; init; }
    public bool IsNonBillSheet { get; init; }
    public string DetectedCurrency { get; init; } = "EGP";
    public string SourceFileName { get; init; } = string.Empty;

    public string TypeBadgeText => IsProvisionalSum 
        ? "مبلغ احتياطي (PS)" 
        : (IsNonBillSheet ? "شيت إداري / تمهيدي" : "جدول كميات وأسعار");
}

/// <summary>
/// Represents intelligent algorithmic mapping between a target consultant worksheet and a contractor source table.
/// </summary>
public sealed record SheetLinkMapping
{
    public required string TargetSheetName { get; init; }
    public required string TargetBillCode { get; init; }
    public int TargetItemCount { get; init; }
    public int TargetStartRow { get; init; } = 1;
    public int SourceStartRow { get; set; } = 1;
    public bool IsProvisionalSum { get; init; }
    public string SelectedSourceFile { get; set; } = string.Empty;
    public string SelectedSourceSheet { get; set; } = string.Empty;
    public double MatchScore { get; set; } = 1.0;
    public SheetLinkStatus Status { get; set; } = SheetLinkStatus.AutoMatched;

    public string DisplayStatusText => Status switch
    {
        SheetLinkStatus.ShieldedPS => "محمي تلقائياً (PS)",
        SheetLinkStatus.AutoMatched => $"تطابق ذكي ({MatchScore:P0})",
        SheetLinkStatus.ManualMatched => "ربط يدوي مخصص",
        SheetLinkStatus.GlobalSearch => "بحث شامل عبر كل الجداول",
        _ => "غير مرتبط"
    };
}

/// <summary>
/// Represents an available column candidate for binding rates, quantities, and descriptions.
/// </summary>
public sealed record ColumnBindingOption
{
    public required int ColumnIndex { get; init; }
    public required string ColumnLetter { get; init; }
    public required string HeaderName { get; init; }

    public string DisplayText => string.IsNullOrWhiteSpace(HeaderName) 
        ? $"العمود {ColumnLetter} (رقم {ColumnIndex + 1})" 
        : $"العمود {ColumnLetter} ({ColumnIndex + 1}): {HeaderName}";
}

/// <summary>
/// Configuration model for column-level routing between source rates and target schedules.
/// </summary>
public sealed record ColumnMappingModel
{
    public int SourceRateColumn { get; set; } = 17;     // Column R (Contractor flat default)
    public int TargetRateColumn { get; set; } = 6;      // Column G (Consultant schedule default)

    public int SourceDescColumn { get; set; } = 11;     // Column L
    public int TargetDescColumn { get; set; } = 2;      // Column C

    public int SourceCodeColumn { get; set; } = 10;     // Column K
    public int TargetCodeColumn { get; set; } = 0;      // Column A

    public int SourceQtyColumn { get; set; } = 14;      // Column O
    public int TargetQtyColumn { get; set; } = 4;       // Column E

    public int SourceUnitColumn { get; set; } = 13;     // Column N
    public int TargetUnitColumn { get; set; } = 5;      // Column F

    public bool IsAutoDetected { get; set; } = true;
    public string SummaryText { get; set; } = "تم تعيين القنوات تلقائياً بناءً على فحص العناوين.";
}

/// <summary>
/// Visual pipeline step describing an algorithm stage in the execution plan.
/// </summary>
public sealed record AlgorithmicPlanStep
{
    public int StepNumber { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Icon { get; init; }
    public required string StatusText { get; init; }
    public bool IsActive { get; init; } = true;
    public required string Details { get; init; }
}
