using System.Collections.ObjectModel;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// ViewModel representing a single sheet/table link mapping between consultant schedule and contractor sources.
/// Follows Clean Code MVVM principles with robust defensive fallback to prevent unlinked exclusions.
/// </summary>
public sealed class SheetLinkMappingViewModel : ViewModelBase
{
    private string _selectedSourceSheet;
    private SheetLinkStatus _status;
    private double _matchScore;

    public string TargetSheetName { get; }
    public string TargetBillCode { get; }
    public int TargetItemCount { get; }
    public int TargetStartRow { get; }
    public int SourceStartRow { get; set; }
    public bool IsProvisionalSum { get; }
    public bool IsNotProvisionalSum => !IsProvisionalSum;
    public string SelectedSourceFile { get; set; }

    public ObservableCollection<string> AvailableOptions { get; }
    public ObservableCollection<string> AvailableSourceOptions => AvailableOptions;

    public SheetLinkMappingViewModel(SheetLinkMapping mapping, IEnumerable<string> sourceOptions)
    {
        TargetSheetName = mapping.TargetSheetName;
        TargetBillCode = mapping.TargetBillCode;
        TargetItemCount = mapping.TargetItemCount;
        TargetStartRow = mapping.TargetStartRow;
        SourceStartRow = mapping.SourceStartRow;
        IsProvisionalSum = mapping.IsProvisionalSum;
        SelectedSourceFile = mapping.SelectedSourceFile;
        _status = mapping.Status;
        _matchScore = mapping.MatchScore;

        AvailableOptions = new ObservableCollection<string>(sourceOptions);

        // Ensure standard options exist in the list
        const string globalOption = "[مطابقة عامة في كامل المقايسة]";
        const string skipOption = "[استثناء هذا الجدول / Skip]";

        if (!AvailableOptions.Contains(globalOption))
        {
            AvailableOptions.Insert(0, globalOption);
        }
        if (!AvailableOptions.Contains(skipOption))
        {
            AvailableOptions.Add(skipOption);
        }

        // Defensive resolution of selected source sheet
        if (IsProvisionalSum)
        {
            _selectedSourceSheet = "محمي تلقائياً (PS Shielded)";
            _status = SheetLinkStatus.ShieldedPS;
            _matchScore = 1.0;
        }
        else if (string.IsNullOrWhiteSpace(mapping.SelectedSourceSheet) || 
                 mapping.Status == SheetLinkStatus.GlobalSearch ||
                 mapping.SelectedSourceSheet.Contains("Global", StringComparison.OrdinalIgnoreCase) ||
                 mapping.SelectedSourceSheet.Contains("شامل", StringComparison.OrdinalIgnoreCase) ||
                 mapping.SelectedSourceSheet.Contains("عامة", StringComparison.OrdinalIgnoreCase))
        {
            _selectedSourceSheet = globalOption;
            _status = SheetLinkStatus.GlobalSearch;
            _matchScore = 0.85;
        }
        else
        {
            _selectedSourceSheet = mapping.SelectedSourceSheet;
            if (!AvailableOptions.Contains(_selectedSourceSheet))
            {
                AvailableOptions.Add(_selectedSourceSheet);
            }
        }
    }

    public string SelectedSourceSheet
    {
        get => _selectedSourceSheet;
        set
        {
            if (SetProperty(ref _selectedSourceSheet, value))
            {
                UpdateStatusFromSelection();
            }
        }
    }

    public SheetLinkStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusBadge));
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(StatusBadgeBackground));
                OnPropertyChanged(nameof(StatusBadgeBorder));
            }
        }
    }

    public double MatchScore
    {
        get => _matchScore;
        set
        {
            if (SetProperty(ref _matchScore, value))
            {
                OnPropertyChanged(nameof(StatusBadge));
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public string StatusBadge => IsProvisionalSum
        ? "مبلغ احتياطي محمي (PS)"
        : Status switch
        {
            SheetLinkStatus.AutoMatched => $"تطابق جدول ({MatchScore:P0})",
            SheetLinkStatus.ManualMatched => "ربط مخصص",
            SheetLinkStatus.GlobalSearch => "مطابقة عامة في المقايسة",
            SheetLinkStatus.Excluded => "مستثنى من التسعير",
            _ => "مطابقة عامة في المقايسة"
        };

    public string StatusBadgeText => StatusBadge;

    public string StatusBadgeColor => IsProvisionalSum
        ? "#F59E0B" // Warm Amber
        : Status switch
        {
            SheetLinkStatus.AutoMatched => "#34D399", // Soft Mint / Emerald
            SheetLinkStatus.ManualMatched => "#38BDF8", // Sky Steel
            SheetLinkStatus.GlobalSearch => "#A5B4FC", // Clean Crisp Indigo Slate
            SheetLinkStatus.Excluded => "#F87171", // Soft Crimson
            _ => "#94A3B8" // Slate
        };

    public string StatusBadgeBackground => IsProvisionalSum
        ? "#261E14"
        : Status switch
        {
            SheetLinkStatus.AutoMatched => "#13261C",
            SheetLinkStatus.ManualMatched => "#122433",
            SheetLinkStatus.GlobalSearch => "#1A2035",
            SheetLinkStatus.Excluded => "#2D1818",
            _ => "#1A1D26"
        };

    public string StatusBadgeBorder => IsProvisionalSum
        ? "#5A3E1B"
        : Status switch
        {
            SheetLinkStatus.AutoMatched => "#204A33",
            SheetLinkStatus.ManualMatched => "#1E4260",
            SheetLinkStatus.GlobalSearch => "#374366",
            SheetLinkStatus.Excluded => "#5B2424",
            _ => "#323746"
        };

    private void UpdateStatusFromSelection()
    {
        if (IsProvisionalSum)
        {
            Status = SheetLinkStatus.ShieldedPS;
            MatchScore = 1.0;
            return;
        }

        // Only explicitly mark Excluded if user picked the Skip option
        if (!string.IsNullOrEmpty(_selectedSourceSheet) &&
            (_selectedSourceSheet.Contains("استثناء", StringComparison.OrdinalIgnoreCase) ||
             _selectedSourceSheet.Contains("Skip", StringComparison.OrdinalIgnoreCase) ||
             _selectedSourceSheet.Contains("Exclude", StringComparison.OrdinalIgnoreCase)))
        {
            Status = SheetLinkStatus.Excluded;
            MatchScore = 0.0;
        }
        else if (string.IsNullOrWhiteSpace(_selectedSourceSheet) || 
                 _selectedSourceSheet.Contains("عامة", StringComparison.OrdinalIgnoreCase) || 
                 _selectedSourceSheet.Contains("شامل", StringComparison.OrdinalIgnoreCase) || 
                 _selectedSourceSheet.Contains("Global", StringComparison.OrdinalIgnoreCase))
        {
            // Default safe mode: Search across all contractor tables (NEVER exclude!)
            Status = SheetLinkStatus.GlobalSearch;
            MatchScore = 0.85;
        }
        else
        {
            Status = SheetLinkStatus.ManualMatched;
            MatchScore = 1.0;
        }
    }

    public SheetLinkMapping ToModel()
    {
        return new SheetLinkMapping
        {
            TargetSheetName = TargetSheetName,
            TargetBillCode = TargetBillCode,
            TargetItemCount = TargetItemCount,
            TargetStartRow = TargetStartRow,
            SourceStartRow = SourceStartRow,
            IsProvisionalSum = IsProvisionalSum,
            SelectedSourceFile = SelectedSourceFile,
            SelectedSourceSheet = SelectedSourceSheet,
            MatchScore = MatchScore,
            Status = Status
        };
    }
}
