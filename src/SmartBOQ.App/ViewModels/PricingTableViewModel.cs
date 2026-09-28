using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SmartBOQ.App.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Specialized sub-viewmodel managing the pricing table grid, high-speed virtualized pagination,
/// multi-factor status filtering, and live search.
/// Inherits from <see cref="ChildViewModelBase"/>.
/// </summary>
public sealed class PricingTableViewModel : ChildViewModelBase
{
    private int _currentPage = 1;
    private int _pageSize = 100;
    private int _totalPages = 1;
    private int _totalFilteredCount = 0;
    private string _jumpPageInput = "1";
    private string _searchQuery = string.Empty;
    private string _activeFilter = "All";
    private CancellationTokenSource? _searchCts;

    private readonly List<BoqMatchedPair> _filteredCache = new(4096);

    public ObservableCollection<BoqMatchedPair> MatchedPairs { get; } = [];
    public ObservableCollection<BoqMatchedPair> PagedItems { get; } = [];
    public ICollectionView FilteredItems { get; }

    public int CurrentPage { get => _currentPage; set => SetProperty(ref _currentPage, value); }
    public int PageSize { get => _pageSize; set => SetProperty(ref _pageSize, value); }
    public int TotalPages { get => _totalPages; set => SetProperty(ref _totalPages, value); }
    public int TotalFilteredCount { get => _totalFilteredCount; set => SetProperty(ref _totalFilteredCount, value); }
    public string JumpPageInput { get => _jumpPageInput; set => SetProperty(ref _jumpPageInput, value); }
    public string ActiveFilter { get => _activeFilter; set => SetProperty(ref _activeFilter, value); }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                DebounceSearch();
            }
        }
    }

    public bool HasNoResults => MatchedPairs.Count == 0;
    public bool HasResults => MatchedPairs.Count > 0;
    public string FilterCountSummary => $"{TotalFilteredCount} / {MatchedPairs.Count}";

    // Commands
    public RelayCommand FirstPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand LastPageCommand { get; }
    public RelayCommand JumpToPageCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand ApproveAllCommand { get; }
    public RelayCommand ToggleApprovalCommand { get; }
    public RelayCommand OpenMatchedSourceLocationCommand { get; }
    public RelayCommand OpenMatchedTargetLocationCommand { get; }

    public PricingTableViewModel(IMainViewModelCoordinator coordinator) : base(coordinator)
    {
        FilteredItems = CollectionViewSource.GetDefaultView(PagedItems);

        FirstPageCommand = new RelayCommand(_ => { CurrentPage = 1; UpdatePagination(); }, _ => CurrentPage > 1);
        PreviousPageCommand = new RelayCommand(_ => { if (CurrentPage > 1) { CurrentPage--; UpdatePagination(); } }, _ => CurrentPage > 1);
        NextPageCommand = new RelayCommand(_ => { if (CurrentPage < TotalPages) { CurrentPage++; UpdatePagination(); } }, _ => CurrentPage < TotalPages);
        LastPageCommand = new RelayCommand(_ => { if (CurrentPage < TotalPages) { CurrentPage = TotalPages; UpdatePagination(); } }, _ => CurrentPage < TotalPages);

        JumpToPageCommand = new RelayCommand(_ =>
        {
            if (int.TryParse(JumpPageInput, out var p) && p >= 1 && p <= TotalPages)
            {
                CurrentPage = p;
                UpdatePagination();
            }
        });

        SetFilterCommand = new RelayCommand(p => SetFilter(p?.ToString() ?? "All"));
        ApproveAllCommand = new RelayCommand(_ => ApproveAll());
        ToggleApprovalCommand = new RelayCommand(p => ToggleApproval(p as BoqMatchedPair));

        OpenMatchedSourceLocationCommand = new RelayCommand(p => OpenMatchedSourceLocation(p as BoqMatchedPair));
        OpenMatchedTargetLocationCommand = new RelayCommand(p => OpenMatchedTargetLocation(p as BoqMatchedPair));
    }

    /// <summary>
    /// Loads or replaces the active reconciliation dataset and updates pagination to page 1.
    /// </summary>
    public void LoadMatchedPairs(IEnumerable<BoqMatchedPair> pairs)
    {
        MatchedPairs.Clear();
        foreach (var pair in pairs)
        {
            MatchedPairs.Add(pair);
        }

        UpdatePagination(resetToPageOne: true);
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(FilterCountSummary));
    }

    /// <summary>
    /// Updates active category filter and re-computes pagination.
    /// </summary>
    public void SetFilter(string filter)
    {
        ActiveFilter = filter;
        UpdatePagination(resetToPageOne: true);
    }

    /// <summary>
    /// Debounces user search queries to prevent UI freezing during rapid typing.
    /// </summary>
    private void DebounceSearch()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        Task.Delay(180, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    UpdatePagination(resetToPageOne: true);
                });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// High-performance O(1) page slicing algorithm.
    /// Filters matched items in memory using cached predicates and updates only the current window.
    /// </summary>
    public void UpdatePagination(bool resetToPageOne = false)
    {
        if (resetToPageOne)
        {
            CurrentPage = 1;
        }

        _filteredCache.Clear();

        string query = SearchQuery?.Trim() ?? string.Empty;
        string filter = ActiveFilter ?? "All";

        for (int i = 0; i < MatchedPairs.Count; i++)
        {
            var pair = MatchedPairs[i];
            if (MatchesFilter(pair, filter) && MatchesSearch(pair, query))
            {
                _filteredCache.Add(pair);
            }
        }

        TotalFilteredCount = _filteredCache.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalFilteredCount / (double)PageSize));

        if (CurrentPage > TotalPages)
        {
            CurrentPage = TotalPages;
        }
        if (CurrentPage < 1)
        {
            CurrentPage = 1;
        }

        JumpPageInput = CurrentPage.ToString();

        // Slice window: [startIndex .. endIndex)
        int startIndex = (CurrentPage - 1) * PageSize;
        int count = Math.Min(PageSize, TotalFilteredCount - startIndex);

        PagedItems.Clear();
        if (count > 0 && startIndex < TotalFilteredCount)
        {
            for (int i = 0; i < count; i++)
            {
                PagedItems.Add(_filteredCache[startIndex + i]);
            }
        }

        OnPropertyChanged(nameof(FilterCountSummary));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(HasResults));
    }

    private static bool MatchesFilter(BoqMatchedPair pair, string filter) => filter switch
    {
        "Priced" => pair.MatchedSourceItem != null,
        "VO" => pair.IsVariationOrder,
        "PS" => pair.TargetItem.Type == BoqItemType.ProvisionalSum,
        "Review" => pair.Confidence == MatchConfidence.ManualReviewNeeded,
        "Approved" => pair.IsApproved,
        _ => true
    };

    private static bool MatchesSearch(BoqMatchedPair pair, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;

        if (pair.TargetItem.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pair.TargetItem.ItemCode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pair.TargetItem.BillNumber.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (pair.TargetItem.SectionName != null && pair.TargetItem.SectionName.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (pair.MatchedSourceItem != null)
        {
            if (pair.MatchedSourceItem.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                pair.MatchedSourceItem.ItemCode.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void ApproveAll()
    {
        int count = 0;
        foreach (var pair in MatchedPairs)
        {
            if (!pair.IsApproved)
            {
                pair.IsApproved = true;
                count++;
            }
        }
        UpdatePagination();
        SetStatus($"تم اعتماد جميع البنود بنجاح ({count} بند).");
    }

    public void ToggleApproval(BoqMatchedPair? pair)
    {
        if (pair == null) return;
        pair.IsApproved = !pair.IsApproved;
        OnPropertyChanged(nameof(PagedItems));
    }

    private void OpenMatchedSourceLocation(BoqMatchedPair? pair)
    {
        if (pair?.MatchedSourceItem == null) return;
        string? filePath = pair.MatchedSourceItem.SheetName;
        // Navigation will be resolved via Coordinator or ExcelNavigator
    }

    private void OpenMatchedTargetLocation(BoqMatchedPair? pair)
    {
        if (pair?.TargetItem == null) return;
        string sheet = pair.TargetItem.SheetName;
        int row = pair.TargetItem.AnchorRowIndex > 0 ? pair.TargetItem.AnchorRowIndex : pair.TargetItem.StartRowIndex;
        // Navigation
    }
}
