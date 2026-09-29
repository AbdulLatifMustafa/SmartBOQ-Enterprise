using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SmartBOQ.App.Services;
using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Export;
using SmartBOQ.Infrastructure.Matching;
using SmartBOQ.Infrastructure.Parsers;
using SmartBOQ.Infrastructure.Storage;
using SmartBOQ.Infrastructure.Verification;
using SmartBOQ.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace SmartBOQ.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IMainViewModelCoordinator
{
    private readonly BoqReconciliationService _service;
    private readonly ILocalizationService _loc;
    private readonly IBoqInspector _inspector;

    public BoqReconciliationService Service => _service;
    public ILocalizationService Localization => _loc;
    public IBoqInspector Inspector => _inspector;
    public string DatabaseFilePath { get; }

    public ComparePipelineViewModel Compare { get; }
    public PricingTableViewModel Pricing { get; }
    public HistoricalRatesViewModel Historical { get; }
    public ProjectSummaryViewModel Summary { get; }
    public ExportFileViewModel Export { get; }

    private string _fileAPath = string.Empty;
    private string _fileBPath = string.Empty;
    private string _outputFilePath = string.Empty;
    private string _dashboardFilePath = string.Empty;

    private bool _isExported;
    private string _lastExportedSchedulePath = string.Empty;
    private string _lastExportedDashboardPath = string.Empty;
    private string _lastExportedFolder = string.Empty;
    private string _zipOutputFilePath = string.Empty;
    private string _lastExportedZipPath = string.Empty;
    private bool _isZipExported;

    private bool _isLoading;
    private int _progressPercentage;
    private string _statusMessage = "Ready";
    private string _searchQuery = string.Empty;
    private string _activeFilter = "All";
    private double _sensitivity = 0.85;
    private FlowDirection _uiFlowDirection = FlowDirection.RightToLeft;
    private int _selectedTabIndex = 0;

    // Advanced Algorithmic Sub-Tab & Model State
    private int _selectedCompareSubTab = 0;
    private BoqFileInfo? _selectedFileForDetails;
    private ColumnMappingModel _activeColumnMapping = new();
    private string _scopeStrategy = "TableIsolated"; // TableIsolated vs GlobalSearch
    private bool _priorityCodeMatching = true;
    private bool _shieldProvisionalSums = true;
    private bool _strictCurrencyIsolation = true;

    // Selected Column Routing Channels
    private ColumnBindingOption? _selectedSourceRateCol;
    private ColumnBindingOption? _selectedTargetRateCol;
    private ColumnBindingOption? _selectedSourceDescCol;
    private ColumnBindingOption? _selectedTargetDescCol;
    private ColumnBindingOption? _selectedSourceCodeCol;
    private ColumnBindingOption? _selectedTargetCodeCol;
    private ColumnBindingOption? _selectedSourceQtyCol;
    private ColumnBindingOption? _selectedTargetQtyCol;
    private ColumnBindingOption? _selectedSourceUnitCol;
    private ColumnBindingOption? _selectedTargetUnitCol;

    private VerificationReport? _verificationReport;
    private ReconciliationResult? _reconciliationResult;

    // Core Grid Collections
    public ObservableCollection<BoqMatchedPair> MatchedPairs { get; } = [];
    public ICollectionView FilteredItems { get; }
    public ObservableCollection<BoqMatchedPair> PagedItems { get; } = [];

    // Multi-Target Reconciliation State & Filtering
    public ObservableCollection<string> TargetFilterOptions { get; } = [];
    private string _selectedTargetFilter = string.Empty;
    public string SelectedTargetFilter
    {
        get => _selectedTargetFilter;
        set
        {
            if (SetField(ref _selectedTargetFilter, value))
            {
                ApplyTargetFilter();
            }
        }
    }
    public bool HasMultipleTargets => TargetFilterOptions.Count > 1;

    private readonly List<BoqMatchedPair> _allReconciledPairs = [];
    private readonly Dictionary<string, ReconciliationResult> _targetResultsMap = new(StringComparer.OrdinalIgnoreCase);

    // Pagination for BOQ Items (100 items per page)
    private int _currentPage = 1;
    private int _pageSize = 100;
    private int _totalPages = 1;
    private int _totalFilteredCount = 0;
    private string _jumpPageInput = "1";
    private CancellationTokenSource? _searchCts;

    // Historical Rates Search State
    private string _historicalSearchQuery = string.Empty;
    private bool _isSearchingHistorical;
    private CancellationTokenSource? _historicalSearchCts;
    public ObservableCollection<HistoricalRateItem> HistoricalSearchResults { get; } = [];

    // Multi-File & Table Inspection Collections
    public ObservableCollection<BoqFileInfo> IngestedFiles { get; } = [];
    public ObservableCollection<BoqSheetSummary> InspectedSheets { get; } = [];

    // Sheet Linker Collections
    public ObservableCollection<SheetLinkMappingViewModel> SheetLinkMappings { get; } = [];
    public ObservableCollection<string> AvailableSourceSheetOptions { get; } = [];

    // Column Mapping Collections
    public ObservableCollection<ColumnBindingOption> AvailableSourceColumns { get; } = [];
    public ObservableCollection<ColumnBindingOption> AvailableTargetColumns { get; } = [];

    // Algorithmic Plan Steps
    public ObservableCollection<AlgorithmicPlanStep> AlgorithmicSteps { get; } = [];

    // User Cancellation
    private CancellationTokenSource? _activeOperationCts;
    public bool CanCancel => _activeOperationCts != null && !_activeOperationCts.IsCancellationRequested;
    public RelayCommand CancelOperationCommand { get; }

    public MainViewModel() : this(
        App.Services?.GetService<BoqReconciliationService>() ?? CreateDefaultService(out var dbPath),
        App.Services?.GetService<ILocalizationService>() ?? CreateDefaultLocalization(),
        App.Services?.GetService<IBoqInspector>() ?? new BoqInspectorService(),
        App.Services != null ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smartboq.db") : null)
    {
    }

    public MainViewModel(
        BoqReconciliationService service,
        ILocalizationService localization,
        IBoqInspector inspector,
        string? databaseFilePath = null)
    {
        _loc = localization;
        _inspector = inspector;
        _service = service;
        DatabaseFilePath = databaseFilePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smartboq.db");

        // Instantiate Specialized Child ViewModels via OOP Coordination
        Compare = new ComparePipelineViewModel(this);
        Pricing = new PricingTableViewModel(this);
        Historical = new HistoricalRatesViewModel(this);
        Summary = new ProjectSummaryViewModel(this);
        Export = new ExportFileViewModel(this);

        FilteredItems = CollectionViewSource.GetDefaultView(PagedItems);

        CancelOperationCommand = new RelayCommand(_ => CancelActiveOperation());

        // Pre-initialize SQLite schema and ensure migrations run immediately on startup
        _ = Task.Run(async () =>
        {
            try
            {
                await _service.InitializeDatabaseAsync();
            }
            catch
            {
                // Ignored
            }
        });

        // Initialize Base Commands
        BrowseFileACommand = new RelayCommand(BrowseFileA);
        BrowseFileBCommand = new RelayCommand(BrowseFileB);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        VerifyCommand = new AsyncRelayCommand(ExecuteVerifyAsync);
        ReconcileCommand = new AsyncRelayCommand(ExecuteReconcileAsync);
        ReconcileAndExportCommand = new AsyncRelayCommand(ExecuteReconcileAndExportAsync);
        ExportCommand = new AsyncRelayCommand(ExecuteExportAsync);
        ExportAllZipCommand = new AsyncRelayCommand(ExecuteExportAllZipAsync);
        BrowseZipOutputCommand = new RelayCommand(_ => BrowseZipOutput());
        OpenZipFileCommand = new RelayCommand(_ => OpenZipFile());
        OpenReconciledFileCommand = new RelayCommand(OpenPricedFile);
        OpenDashboardCommand = new RelayCommand(OpenDashboard);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        SetFilterCommand = new RelayCommand(p => SetFilter(p?.ToString() ?? "All"));
        ApproveAllCommand = new RelayCommand(ApproveAll);

        // Initialize Algorithmic Commands
        AddNewFileCommand = new AsyncRelayCommand(AddNewFileAsync);
        RemoveFileCommand = new RelayCommand(p => RemoveFile(p as BoqFileInfo));
        SetPrimaryTargetCommand = new RelayCommand(p => SetPrimaryTarget(p as BoqFileInfo));
        SetPrimaryContractorCommand = new RelayCommand(p => SetPrimarySource(p as BoqFileInfo));
        SetPrimaryConsultantCommand = new RelayCommand(p => SetPrimaryTarget(p as BoqFileInfo));
        AutoLinkSheetsCommand = new AsyncRelayCommand(AutoLinkSheetsAsync);
        LinkAllGlobalCommand = new RelayCommand(LinkAllGlobal);
        AutoDetectColumnsCommand = new AsyncRelayCommand(AutoDetectColumnsAsync);
        InspectFileCommand = new RelayCommand(p => InspectFile(p as BoqFileInfo));
        SelectCompareSubTabCommand = new RelayCommand(p => { if (int.TryParse(p?.ToString(), out var idx)) SelectedCompareSubTab = idx; });

        // Initialize Pagination Commands (100 items per page)
        NextPageCommand = new RelayCommand(_ => { if (CurrentPage < TotalPages) { CurrentPage++; UpdatePagination(); } });
        PreviousPageCommand = new RelayCommand(_ => { if (CurrentPage > 1) { CurrentPage--; UpdatePagination(); } });
        FirstPageCommand = new RelayCommand(_ => { if (CurrentPage > 1) { CurrentPage = 1; UpdatePagination(); } });
        LastPageCommand = new RelayCommand(_ => { if (CurrentPage < TotalPages) { CurrentPage = TotalPages; UpdatePagination(); } });
        JumpToPageCommand = new RelayCommand(_ => 
        { 
            if (int.TryParse(JumpPageInput, out var p) && p >= 1 && p <= TotalPages) 
            { 
                CurrentPage = p; 
                UpdatePagination(); 
            } 
        });

        // Initialize Historical Search & Excel Navigation Commands
        SearchHistoricalCommand = new RelayCommand(_ => _ = ExecuteHistoricalSearchAsync(HistoricalSearchQuery));
        ClearHistoricalSearchCommand = new RelayCommand(_ => { HistoricalSearchQuery = string.Empty; _ = ExecuteHistoricalSearchAsync(string.Empty); });
        RefreshHistoricalCommand = new RelayCommand(_ => _ = ExecuteHistoricalSearchAsync(HistoricalSearchQuery));
        OpenSheetLocationCommand = new RelayCommand(p => OpenSheetLocation(p as BoqSheetSummary));
        OpenLinkedSheetLocationCommand = new RelayCommand(p => OpenLinkedSheetLocation(p as SheetLinkMappingViewModel));
        OpenLinkedSourceSheetLocationCommand = new RelayCommand(p => OpenLinkedSourceSheetLocation(p as SheetLinkMappingViewModel));
        OpenMatchedSourceLocationCommand = new RelayCommand(p => OpenMatchedSourceLocation(p as BoqMatchedPair));
        OpenMatchedTargetLocationCommand = new RelayCommand(p => OpenMatchedTargetLocation(p as BoqMatchedPair));
        OpenHistoricalItemLocationCommand = new RelayCommand(p => OpenHistoricalItemLocation(p as HistoricalRateItem));
        OpenDatabaseLocationCommand = new RelayCommand(_ => OpenDatabaseLocation());

        UpdatePagination(resetToPageOne: true);

        // Auto-detect default workspace files and trigger inspection
        InitializeDefaultFiles();
    }

    #region IMainViewModelCoordinator Implementation

    public void SetStatus(string message, int progress = -1)
    {
        StatusMessage = message;
        if (progress >= 0)
        {
            ProgressPercentage = progress;
        }
    }

    public void SetBusy(bool isBusy)
    {
        IsLoading = isBusy;
    }

    public async Task ExecuteWithBusyIndicatorAsync(Func<Task> action, string initialStatus = "Processing...")
    {
        IsLoading = true;
        StatusMessage = initialStatus;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ: {ex.Message}";
            MessageBox.Show(ex.Message, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region Properties

    public string FileAPath { get => _fileAPath; set => SetField(ref _fileAPath, value); }
    public string FileBPath
    {
        get => _fileBPath;
        set
        {
            if (SetField(ref _fileBPath, value))
            {
                UpdateDefaultOutputPath();
            }
        }
    }
    public string OutputFilePath { get => _outputFilePath; set => SetField(ref _outputFilePath, value); }
    public string DashboardFilePath { get => _dashboardFilePath; set => SetField(ref _dashboardFilePath, value); }

    public bool IsExported { get => _isExported; set => SetField(ref _isExported, value); }
    public string LastExportedSchedulePath { get => _lastExportedSchedulePath; set => SetField(ref _lastExportedSchedulePath, value); }
    public string LastExportedDashboardPath { get => _lastExportedDashboardPath; set => SetField(ref _lastExportedDashboardPath, value); }
    public string LastExportedFolder { get => _lastExportedFolder; set => SetField(ref _lastExportedFolder, value); }
    public bool IsZipExported { get => _isZipExported; set => SetField(ref _isZipExported, value); }
    public string ZipOutputFilePath { get => _zipOutputFilePath; set => SetField(ref _zipOutputFilePath, value); }
    public string LastExportedZipPath { get => _lastExportedZipPath; set => SetField(ref _lastExportedZipPath, value); }
    public string DefaultExportDirectoryDisplay => GetDefaultExportDirectory();

    public static string GetDefaultExportDirectory()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string exportDir = Path.Combine(baseDir, "Exported_BOQs");
        if (!Directory.Exists(exportDir))
        {
            try { Directory.CreateDirectory(exportDir); } catch { }
        }
        return Directory.Exists(exportDir) ? exportDir : baseDir;
    }

    public bool IsLoading { get => _isLoading; set => SetField(ref _isLoading, value); }
    public int ProgressPercentage { get => _progressPercentage; set => SetField(ref _progressPercentage, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            if (SetField(ref _sensitivity, value))
            {
                BuildAlgorithmicPlan();
            }
        }
    }
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetField(ref _selectedTabIndex, value))
            {
                if (value == 3) // Tab 4: Historical Project Rates
                {
                    _ = LoadHistoricalRatesIfEmptyAsync();
                }
            }
        }
    }

    private string _invoiceOrProjectName = string.Empty;
    public string InvoiceOrProjectName { get => _invoiceOrProjectName; set => SetField(ref _invoiceOrProjectName, value); }

    public string HistoricalSearchQuery
    {
        get => _historicalSearchQuery;
        set
        {
            if (SetField(ref _historicalSearchQuery, value))
            {
                DebounceHistoricalSearch();
            }
        }
    }

    public bool IsSearchingHistorical 
    { 
        get => _isSearchingHistorical; 
        set 
        { 
            if (SetField(ref _isSearchingHistorical, value))
            {
                OnPropertyChanged(nameof(HasNoHistoricalResults));
            }
        } 
    }
    public int HistoricalTotalCount => HistoricalSearchResults.Count;
    public bool HasNoHistoricalResults => HistoricalSearchResults.Count == 0 && !IsSearchingHistorical;
    public bool HasHistoricalResults => HistoricalSearchResults.Count > 0;
    public decimal HistoricalAvgRate => HistoricalSearchResults.Where(x => x.UnitRate.HasValue).Select(x => x.UnitRate!.Value).DefaultIfEmpty(0m).Average();
    public string HistoricalLatestDate => HistoricalSearchResults.FirstOrDefault()?.FormattedDate ?? "-";

    public int SelectedCompareSubTab
    {
        get => _selectedCompareSubTab;
        set => SetField(ref _selectedCompareSubTab, value);
    }

    public BoqFileInfo? SelectedFileForDetails
    {
        get => _selectedFileForDetails;
        set => SetField(ref _selectedFileForDetails, value);
    }

    public ColumnMappingModel ActiveColumnMapping
    {
        get => _activeColumnMapping;
        set => SetField(ref _activeColumnMapping, value);
    }

    public string ScopeStrategy
    {
        get => _scopeStrategy;
        set
        {
            if (SetField(ref _scopeStrategy, value))
            {
                OnPropertyChanged(nameof(SampleRoutedDataText));
                BuildAlgorithmicPlan();
            }
        }
    }

    public bool PriorityCodeMatching
    {
        get => _priorityCodeMatching;
        set
        {
            if (SetField(ref _priorityCodeMatching, value))
            {
                BuildAlgorithmicPlan();
            }
        }
    }

    public bool ShieldProvisionalSums
    {
        get => _shieldProvisionalSums;
        set
        {
            if (SetField(ref _shieldProvisionalSums, value))
            {
                BuildAlgorithmicPlan();
            }
        }
    }

    public bool StrictCurrencyIsolation
    {
        get => _strictCurrencyIsolation;
        set
        {
            if (SetField(ref _strictCurrencyIsolation, value))
            {
                BuildAlgorithmicPlan();
            }
        }
    }

    // Column Channel Selection Properties
    public ColumnBindingOption? SelectedSourceRateCol
    {
        get => _selectedSourceRateCol;
        set
        {
            if (SetField(ref _selectedSourceRateCol, value) && value != null)
            {
                _activeColumnMapping.SourceRateColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
                BuildAlgorithmicPlan();
            }
        }
    }

    public ColumnBindingOption? SelectedTargetRateCol
    {
        get => _selectedTargetRateCol;
        set
        {
            if (SetField(ref _selectedTargetRateCol, value) && value != null)
            {
                _activeColumnMapping.TargetRateColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
                BuildAlgorithmicPlan();
            }
        }
    }

    public ColumnBindingOption? SelectedSourceDescCol
    {
        get => _selectedSourceDescCol;
        set
        {
            if (SetField(ref _selectedSourceDescCol, value) && value != null)
            {
                _activeColumnMapping.SourceDescColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public ColumnBindingOption? SelectedTargetDescCol
    {
        get => _selectedTargetDescCol;
        set
        {
            if (SetField(ref _selectedTargetDescCol, value) && value != null)
            {
                _activeColumnMapping.TargetDescColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public ColumnBindingOption? SelectedSourceCodeCol
    {
        get => _selectedSourceCodeCol;
        set
        {
            if (SetField(ref _selectedSourceCodeCol, value) && value != null)
            {
                _activeColumnMapping.SourceCodeColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public ColumnBindingOption? SelectedTargetCodeCol
    {
        get => _selectedTargetCodeCol;
        set
        {
            if (SetField(ref _selectedTargetCodeCol, value) && value != null)
            {
                _activeColumnMapping.TargetCodeColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public ColumnBindingOption? SelectedSourceQtyCol
    {
        get => _selectedSourceQtyCol;
        set
        {
            if (SetField(ref _selectedSourceQtyCol, value) && value != null)
            {
                _activeColumnMapping.SourceQtyColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public ColumnBindingOption? SelectedTargetQtyCol
    {
        get => _selectedTargetQtyCol;
        set
        {
            if (SetField(ref _selectedTargetQtyCol, value) && value != null)
            {
                _activeColumnMapping.TargetQtyColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public ColumnBindingOption? SelectedSourceUnitCol
    {
        get => _selectedSourceUnitCol;
        set
        {
            if (SetField(ref _selectedSourceUnitCol, value) && value != null)
            {
                _activeColumnMapping.SourceUnitColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public ColumnBindingOption? SelectedTargetUnitCol
    {
        get => _selectedTargetUnitCol;
        set
        {
            if (SetField(ref _selectedTargetUnitCol, value) && value != null)
            {
                _activeColumnMapping.TargetUnitColumn = value.ColumnIndex;
                OnPropertyChanged(nameof(SampleRoutedDataText));
            }
        }
    }

    public string SampleRoutedDataText =>
        $"مسار الربط النشط: " +
        $"الكود [{SelectedTargetCodeCol?.ColumnLetter ?? "A"}] <-- [{SelectedSourceCodeCol?.ColumnLetter ?? "K"}] | " +
        $"الوصف [{SelectedTargetDescCol?.ColumnLetter ?? "C"}] <-- [{SelectedSourceDescCol?.ColumnLetter ?? "L"}] | " +
        $"الكمية [{SelectedTargetQtyCol?.ColumnLetter ?? "E"}] <-- [{SelectedSourceQtyCol?.ColumnLetter ?? "O"}] | " +
        $"سعر الوحدة المستهدف [{SelectedTargetRateCol?.ColumnLetter ?? "G"}] <-- [{SelectedSourceRateCol?.ColumnLetter ?? "R"}] " +
        $"(استراتيجية البحث: {(ScopeStrategy == "TableIsolated" ? "عزل صارم لكل جدول" : "مطابقة عامة في المقايسة")})";

    // KPI Counters for Dashboard
    public int TotalDiscoveredTablesCount => IngestedFiles.Sum(f => f.SheetsCount);
    public int TotalDiscoveredItemsCount => IngestedFiles.Sum(f => f.TotalEstimatedItems);
    public int TotalLinkedTablesCount => SheetLinkMappings.Count(m => m.Status == SheetLinkStatus.AutoMatched || m.Status == SheetLinkStatus.ManualMatched || m.Status == SheetLinkStatus.GlobalSearch);
    public int TotalShieldedTablesCount => SheetLinkMappings.Count(m => m.Status == SheetLinkStatus.ShieldedPS);

    // Preset Routing Delegations
    public ObservableCollection<MappingPreset> SavedPresets => Compare.SavedPresets;
    public MappingPreset? SelectedPreset { get => Compare.SelectedPreset; set => Compare.SelectedPreset = value; }
    public string? NewPresetName { get => Compare.NewPresetName; set => Compare.NewPresetName = value; }
    public AsyncRelayCommand SaveCurrentPresetCommand => Compare.SaveCurrentPresetCommand;
    public RelayCommand ApplyPresetCommand => Compare.ApplyPresetCommand;
    public AsyncRelayCommand DeletePresetCommand => Compare.DeletePresetCommand;
    public AsyncRelayCommand RefreshPresetsCommand => Compare.RefreshPresetsCommand;

    // Audit Trail Methods
    public Task RecordAuditLogAsync(ItemAuditLog log, CancellationToken ct = default) =>
        _service.RecordAuditLogAsync(log, ct);

    public Task<IReadOnlyList<ItemAuditLog>> GetAuditLogsForItemAsync(string itemId, CancellationToken ct = default) =>
        _service.GetAuditLogsForItemAsync(itemId, ct);

    public FlowDirection UiFlowDirection { get => _uiFlowDirection; set => SetField(ref _uiFlowDirection, value); }
    public string CurrentLanguageCode => _loc.CurrentCulture;
    public bool IsArabic => _loc.CurrentCulture.StartsWith("ar", StringComparison.OrdinalIgnoreCase);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                FilteredItems.Refresh();
                UpdatePagination(resetToPageOne: true);
            }
        }
    }

    public string ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (SetField(ref _activeFilter, value))
            {
                FilteredItems.Refresh();
                UpdatePagination(resetToPageOne: true);
            }
        }
    }

    // Pagination properties
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetField(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
                OnPropertyChanged(nameof(CurrentPageDisplay));
                OnPropertyChanged(nameof(PageInfoText));
            }
        }
    }

    public int PageSize
    {
        get => _pageSize;
        set => SetField(ref _pageSize, value);
    }

    public int TotalPages
    {
        get => _totalPages;
        set
        {
            if (SetField(ref _totalPages, value))
            {
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
                OnPropertyChanged(nameof(CurrentPageDisplay));
            }
        }
    }

    public int TotalFilteredCount
    {
        get => _totalFilteredCount;
        set
        {
            if (SetField(ref _totalFilteredCount, value))
            {
                OnPropertyChanged(nameof(PageInfoText));
            }
        }
    }

    public string JumpPageInput
    {
        get => _jumpPageInput;
        set => SetField(ref _jumpPageInput, value);
    }

    public bool CanGoToPreviousPage => CurrentPage > 1;
    public bool CanGoToNextPage => CurrentPage < TotalPages;
    public string CurrentPageDisplay => IsArabic ? $"صفحة {CurrentPage} من {TotalPages}" : $"Page {CurrentPage} of {TotalPages}";

    public string PageInfoText
    {
        get
        {
            if (TotalFilteredCount == 0)
            {
                return IsArabic ? "لا توجد بنود مطابقة للعرض" : "No items to display";
            }
            int startItem = (CurrentPage - 1) * PageSize + 1;
            int endItem = Math.Min(CurrentPage * PageSize, TotalFilteredCount);
            return IsArabic 
                ? $"عرض {startItem:N0} - {endItem:N0} من أصل {TotalFilteredCount:N0} بند" 
                : $"Showing {startItem:N0} - {endItem:N0} of {TotalFilteredCount:N0} items";
        }
    }

    public VerificationReport? VerificationReport
    {
        get => _verificationReport;
        private set
        {
            SetField(ref _verificationReport, value);
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(VerificationSummaryText));
        }
    }

    public bool IsVerified => VerificationReport?.IsValid == true;
    public string VerificationSummaryText => VerificationReport == null 
        ? (IsArabic ? "لم يتم الفحص بعد" : "Not Verified Yet")
        : (VerificationReport.IsValid 
            ? (IsArabic ? "تم التحقق بنجاح من كافة الشروط" : "Schema & Integrity 100% Passed")
            : (IsArabic ? "فشل التحقق: يرجى مراجعة الأخطاء" : "Verification Failed"));

    public ReconciliationResult? Result
    {
        get => _reconciliationResult;
        private set
        {
            SetField(ref _reconciliationResult, value);
            OnPropertyChanged(nameof(TotalItemsCount));
            OnPropertyChanged(nameof(ExactMatchesCount));
            OnPropertyChanged(nameof(VariationOrdersCount));
            OnPropertyChanged(nameof(ProvisionalSumsCount));
            OnPropertyChanged(nameof(TotalInjectedEgpText));
            OnPropertyChanged(nameof(TotalInjectedUsdText));
            OnPropertyChanged(nameof(BaseTenderEgpText));
            OnPropertyChanged(nameof(VarianceEgpText));
        }
    }

    public int TotalItemsCount => Result?.TotalTargetItems ?? 0;
    public int ExactMatchesCount => Result?.ExactMatches ?? 0;
    public int VariationOrdersCount => Result?.VariationOrders ?? 0;
    public int ProvisionalSumsCount => Result?.ProvisionalSumsShielded ?? 0;

    public string TotalInjectedEgpText
    {
        get
        {
            var b = Result?.CurrencySummaries.FirstOrDefault(c => c.Currency.Equals("EGP", StringComparison.OrdinalIgnoreCase));
            return b != null ? $"{b.TotalRemeasureAmount:N2} EGP" : "0.00 EGP";
        }
    }

    public string TotalInjectedUsdText
    {
        get
        {
            var b = Result?.CurrencySummaries.FirstOrDefault(c => c.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase));
            return b != null ? $"${b.TotalRemeasureAmount:N2}" : "$0.00";
        }
    }

    public string BaseTenderEgpText
    {
        get
        {
            var b = Result?.CurrencySummaries.FirstOrDefault(c => c.Currency.Equals("EGP", StringComparison.OrdinalIgnoreCase));
            return b != null ? $"{b.TotalBaseAmount:N2} EGP" : "0.00 EGP";
        }
    }

    public string VarianceEgpText
    {
        get
        {
            var b = Result?.CurrencySummaries.FirstOrDefault(c => c.Currency.Equals("EGP", StringComparison.OrdinalIgnoreCase));
            return b != null ? $"{b.VarianceAmount:N2} EGP ({b.VariancePercentage:+0.00;-0.00}%)" : "0.00 EGP";
        }
    }

    // Localized UI Texts
    public string TabSummary => _loc["Navigation.Summary"];
    public string TabCompare => _loc["Navigation.Compare"];
    public string TabPricingGrid => _loc["Navigation.PricingGrid"];
    public string TabProjectRates => _loc["Navigation.ProjectRates"];
    public string TabExport => _loc["Navigation.Export"];

    public string LblSearchAccuracy => _loc["Navigation.SearchAccuracy"];
    public string LblCalculationMethod => _loc["Navigation.CalculationMethod"];
    public string LblOfflineBadge => IsArabic ? "نظام محلي معزول 100%" : "100% Offline Air-Gapped";

    public bool IsDarkMode => ThemeManager.IsDarkMode;
    public string ThemeIcon => IsDarkMode ? "Sun" : "Moon";
    public string ThemeText => IsArabic ? (IsDarkMode ? "الوضع النهاري" : "الوضع الليلي") : (IsDarkMode ? "Light Mode" : "Dark Mode");

    #endregion

    #region Commands

    public RelayCommand BrowseFileACommand { get; }
    public RelayCommand BrowseFileBCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public AsyncRelayCommand VerifyCommand { get; }
    public AsyncRelayCommand ReconcileCommand { get; }
    public AsyncRelayCommand ReconcileAndExportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ExportAllZipCommand { get; }
    public RelayCommand BrowseZipOutputCommand { get; }
    public RelayCommand OpenZipFileCommand { get; }
    public RelayCommand OpenReconciledFileCommand { get; }
    public RelayCommand OpenDashboardCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand ApproveAllCommand { get; }

    // Pagination Commands (100 items per page)
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand FirstPageCommand { get; }
    public RelayCommand LastPageCommand { get; }
    public RelayCommand JumpToPageCommand { get; }

    // Algorithmic Navigation and Multi-File Commands
    public AsyncRelayCommand AddNewFileCommand { get; }
    public RelayCommand RemoveFileCommand { get; }
    public RelayCommand SetPrimaryTargetCommand { get; }
    public RelayCommand SetPrimaryContractorCommand { get; }
    public RelayCommand SetPrimaryConsultantCommand { get; }
    public AsyncRelayCommand AutoLinkSheetsCommand { get; }
    public RelayCommand LinkAllGlobalCommand { get; }
    public AsyncRelayCommand AutoDetectColumnsCommand { get; }
    public RelayCommand InspectFileCommand { get; }
    public RelayCommand SelectCompareSubTabCommand { get; }
    public RelayCommand SetCompareSubTabCommand => SelectCompareSubTabCommand;

    // Historical Rates & Table Navigation Commands
    public RelayCommand SearchHistoricalCommand { get; }
    public RelayCommand ClearHistoricalSearchCommand { get; }
    public RelayCommand RefreshHistoricalCommand { get; }
    public RelayCommand OpenSheetLocationCommand { get; }
    public RelayCommand OpenLinkedSheetLocationCommand { get; }
    public RelayCommand OpenLinkedSourceSheetLocationCommand { get; }
    public RelayCommand OpenMatchedSourceLocationCommand { get; }
    public RelayCommand OpenMatchedTargetLocationCommand { get; }
    public RelayCommand OpenHistoricalItemLocationCommand { get; }
    public RelayCommand OpenDatabaseLocationCommand { get; }

    #endregion

    #region Base Command Handlers

    public void CancelActiveOperation()
    {
        if (_activeOperationCts != null && !_activeOperationCts.IsCancellationRequested)
        {
            _activeOperationCts.Cancel();
            StatusMessage = IsArabic ? "جارٍ إلغاء العملية بناءً على طلب المستخدم..." : "Cancelling operation by user request...";
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    private static ILocalizationService CreateDefaultLocalization()
    {
        var loc = new LocalizationService();
        loc.SetCulture("ar-EG");
        return loc;
    }

    private static BoqReconciliationService CreateDefaultService(out string dbPath)
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        dbPath = Path.Combine(exeDir, "smartboq.db");
        string oldAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartBOQ", "smartboq.db");
        if (!File.Exists(dbPath) && File.Exists(oldAppData))
        {
            try
            {
                File.Copy(oldAppData, dbPath, overwrite: false);
            }
            catch { }
        }

        return new BoqReconciliationService(
            new PreFlightVerificationGate(),
            new HatchwayFlatReader(),
            new HierarchicalBoqReader(),
            new HybridWeightedMatcher(),
            new ClosedXmlExporter(),
            new SqliteBoqRepository(dbPath),
            new BoqInspectorService()
        );
    }

    private void UpdateDefaultOutputPath()
    {
        if (string.IsNullOrWhiteSpace(_fileBPath)) return;

        string fixedDir = GetDefaultExportDirectory();
        string nameWithoutExt = Path.GetFileNameWithoutExtension(_fileBPath);
        if (nameWithoutExt.EndsWith("_Reconciled", StringComparison.OrdinalIgnoreCase))
        {
            nameWithoutExt = nameWithoutExt[..^11];
        }

        string sourceDir = Path.GetDirectoryName(_fileBPath) ?? string.Empty;

        // Default to the fixed export directory beside the executable
        if (string.IsNullOrWhiteSpace(OutputFilePath) || OutputFilePath.StartsWith(sourceDir, StringComparison.OrdinalIgnoreCase))
        {
            OutputFilePath = Path.Combine(fixedDir, $"{nameWithoutExt}_Reconciled.xlsx");
        }

        string targetDir = !string.IsNullOrWhiteSpace(OutputFilePath) 
            ? (Path.GetDirectoryName(OutputFilePath) ?? fixedDir) 
            : fixedDir;
        DashboardFilePath = Path.Combine(targetDir, $"{nameWithoutExt}_Executive_Dashboard.xlsx");

        if (string.IsNullOrWhiteSpace(ZipOutputFilePath) || ZipOutputFilePath.StartsWith(sourceDir, StringComparison.OrdinalIgnoreCase))
        {
            ZipOutputFilePath = Path.Combine(fixedDir, $"{nameWithoutExt}_All_Reconciled_Packages_{DateTime.Now:yyyyMMdd_HHmm}.zip");
        }
    }

    private void BrowseFileA()
    {
        var dlg = new OpenFileDialog { Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls" };
        if (dlg.ShowDialog() == true)
        {
            FileAPath = dlg.FileName;
            _ = RefreshFilesAsync();
        }
    }

    private void BrowseFileB()
    {
        var dlg = new OpenFileDialog { Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls" };
        if (dlg.ShowDialog() == true)
        {
            FileBPath = dlg.FileName;
            _ = RefreshFilesAsync();
        }
    }

    private void BrowseOutput()
    {
        string defaultDir = GetDefaultExportDirectory();
        string defaultName = !string.IsNullOrWhiteSpace(FileBPath)
            ? $"{Path.GetFileNameWithoutExtension(FileBPath)}_Reconciled.xlsx"
            : "Reconciled_Pricing_Schedule.xlsx";

        var dlg = new SaveFileDialog
        {
            Filter = "Excel Files (*.xlsx)|*.xlsx",
            FileName = defaultName,
            InitialDirectory = defaultDir
        };
        if (dlg.ShowDialog() == true) OutputFilePath = dlg.FileName;
    }

    private async Task ExecuteVerifyAsync()
    {
        if (string.IsNullOrWhiteSpace(FileAPath) || !File.Exists(FileAPath))
        {
            MessageBox.Show(
                IsArabic ? "يرجى تحديد مسار ملف المقاول (File A) أولاً!" : "Please select Contractor Flat BOQ (File A) first!",
                IsArabic ? "تنبيه" : "Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(FileBPath) || !File.Exists(FileBPath))
        {
            MessageBox.Show(
                IsArabic ? "يرجى تحديد مسار ملف الاستشاري (File B) أولاً!" : "Please select Consultant Pricing Schedule (File B) first!",
                IsArabic ? "تنبيه" : "Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _activeOperationCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        try
        {
            var ct = _activeOperationCts.Token;
            IsLoading = true;
            StatusMessage = IsArabic ? "جارٍ فحص الملفات..." : "Verifying files schema & integrity...";
            VerificationReport = await _service.VerifyFilesAsync(FileAPath, FileBPath, ct);
            StatusMessage = VerificationReport.IsValid
                ? (IsArabic ? "اكتمل الفحص بنجاح!" : "Pre-flight verification passed!")
                : (IsArabic ? "يوجد أخطاء في الفحص!" : "Verification failed!");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = IsArabic ? "تم إلغاء فحص الملفات." : "Verification cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            _activeOperationCts?.Dispose();
            _activeOperationCts = null;
            OnPropertyChanged(nameof(CanCancel));
            IsLoading = false;
        }
    }

    private void ApplyTargetFilter()
    {
        if (_allReconciledPairs.Count == 0) return;

        bool isAll = string.IsNullOrWhiteSpace(SelectedTargetFilter) ||
                     SelectedTargetFilter.StartsWith("All", StringComparison.OrdinalIgnoreCase) ||
                     SelectedTargetFilter.StartsWith("الكل", StringComparison.OrdinalIgnoreCase) ||
                     SelectedTargetFilter.StartsWith("جميع", StringComparison.OrdinalIgnoreCase) ||
                     SelectedTargetFilter.StartsWith("(جميع", StringComparison.OrdinalIgnoreCase);

        MatchedPairs.Clear();

        IEnumerable<BoqMatchedPair> source = isAll
            ? _allReconciledPairs
            : _allReconciledPairs.Where(p => p.TargetItem.WorkbookName.Equals(SelectedTargetFilter, StringComparison.OrdinalIgnoreCase));

        var sorted = source
            .OrderByDescending(p => p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum)
            .ThenBy(p => p.IsProvisionalSum)
            .ThenBy(p => p.TargetItem.WorkbookName)
            .ThenBy(p => p.TargetItem.BillNumber)
            .ThenBy(p => p.TargetItem.AnchorRowIndex)
            .ToList();

        foreach (var p in sorted)
        {
            MatchedPairs.Add(p);
        }

        if (!isAll && _targetResultsMap.TryGetValue(SelectedTargetFilter, out var singleResult))
        {
            Result = singleResult;
        }
        else if (isAll && _targetResultsMap.Count > 0)
        {
            Result = CreateAggregateResult(_targetResultsMap.Values);
        }

        FilteredItems.Refresh();
        UpdatePagination(resetToPageOne: true);
    }

    private static ReconciliationResult CreateAggregateResult(IEnumerable<ReconciliationResult> results)
    {
        var list = results.ToList();
        var allPairs = list.SelectMany(r => r.MatchedPairs).ToList();
        var allSheets = list.SelectMany(r => r.TargetSheets).ToList();
        var allSources = list.FirstOrDefault()?.SourceItems ?? new List<BoqItem>();
        var totalTime = TimeSpan.FromMilliseconds(list.Sum(r => r.ElapsedTime.TotalMilliseconds));

        var currMap = new Dictionary<string, (decimal tender, decimal remeasure, int count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in list)
        {
            foreach (var c in r.CurrencySummaries)
            {
                if (!currMap.TryGetValue(c.Currency, out var vals))
                {
                    vals = (0m, 0m, 0);
                }
                currMap[c.Currency] = (vals.tender + c.TotalBaseAmount, vals.remeasure + c.TotalRemeasureAmount, vals.count + c.ItemsCount);
            }
        }

        var summaries = currMap.Select(kvp => new CurrencyBucketSummary
        {
            Currency = kvp.Key,
            TotalBaseAmount = kvp.Value.tender,
            TotalRemeasureAmount = kvp.Value.remeasure,
            ItemsCount = kvp.Value.count
        }).ToList();

        return new ReconciliationResult
        {
            MatchedPairs = allPairs,
            TargetSheets = allSheets,
            SourceItems = allSources,
            CurrencySummaries = summaries,
            ElapsedTime = totalTime
        };
    }

    private async Task ExecuteMultiTargetReconcileAsync(List<string> sourceFilePaths, List<BoqFileInfo> targetFiles, CancellationToken ct)
    {
        IsLoading = true;
        ProgressPercentage = 5;
        StatusMessage = IsArabic 
            ? $"جارٍ فحص وتوزيع الأسعار لـ ({targetFiles.Count}) مقايسات استشارية..." 
            : $"Reconciling line items across ({targetFiles.Count}) consultant schedules...";

        _allReconciledPairs.Clear();
        _targetResultsMap.Clear();
        TargetFilterOptions.Clear();

        int currentTgt = 0;
        var swTotal = Stopwatch.StartNew();

        foreach (var tFile in targetFiles)
        {
            ct.ThrowIfCancellationRequested();
            currentTgt++;
            ProgressPercentage = (int)((double)currentTgt / targetFiles.Count * 90.0);
            StatusMessage = IsArabic 
                ? $"جارٍ مطابقة المقايسة ({currentTgt}/{targetFiles.Count}): {tFile.FileName}..." 
                : $"Matching target ({currentTgt}/{targetFiles.Count}): {tFile.FileName}...";

            var targetResult = await _service.ReconcileMultiSourceAsync(
                sourceFilePaths,
                tFile.FilePath,
                Sensitivity,
                sheetMappings: null,
                columnMappings: (ActiveColumnMapping != null && !ActiveColumnMapping.IsAutoDetected) ? ActiveColumnMapping : null,
                ct: ct);

            foreach (var p in targetResult.MatchedPairs)
            {
                if (p.InjectedRate.HasValue && !p.IsProvisionalSum && p.Confidence != MatchConfidence.ManualReviewNeeded)
                {
                    p.IsApproved = true;
                }
            }

            _targetResultsMap[tFile.FileName] = targetResult;
            _allReconciledPairs.AddRange(targetResult.MatchedPairs);
        }

        swTotal.Stop();
        ProgressPercentage = 100;

        string allOption = IsArabic 
            ? $"جميع المقايسات المستهدفة ({_allReconciledPairs.Count:N0} بند)" 
            : $"All Target BOQs ({_allReconciledPairs.Count:N0} items)";

        TargetFilterOptions.Add(allOption);
        foreach (var tFile in targetFiles)
        {
            TargetFilterOptions.Add(tFile.FileName);
        }

        OnPropertyChanged(nameof(HasMultipleTargets));
        SelectedTargetFilter = allOption;
        ApplyTargetFilter();

        int totalPriced = _allReconciledPairs.Count(p => p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum);
        int totalSheets = _targetResultsMap.Values.Sum(r => r.TargetSheets.Count);

        StatusMessage = IsArabic 
            ? $"اكتملت المطابقة الذكية في {swTotal.Elapsed.TotalSeconds:F2} ثانية ({totalPriced:N0} بند مسعر عبر {totalSheets} جدول في {targetFiles.Count} مقايسة)" 
            : $"Multi-target reconciliation complete in {swTotal.Elapsed.TotalSeconds:F2}s ({totalPriced:N0} priced items across {totalSheets} tables in {targetFiles.Count} BOQs)";

        SelectedTabIndex = 2; // Jump to Pricing Table tab
    }

    private async Task ExecuteReconcileAsync()
    {
        // Gather all candidate source files
        var sourceFilePaths = IngestedFiles
            .Where(f => f.Role != BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
            .Select(f => f.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceFilePaths.Count == 0 && File.Exists(FileAPath))
        {
            sourceFilePaths.Add(FileAPath);
        }

        if (sourceFilePaths.Count == 0)
        {
            MessageBox.Show(
                IsArabic ? "يرجى تحديد مسار ملف المقاول (File A) أولاً!" : "Please select Contractor Flat BOQ (File A) first!",
                IsArabic ? "تنبيه" : "Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var targetFiles = IngestedFiles
            .Where(f => f.Role == BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
            .ToList();

        if (targetFiles.Count == 0 && (string.IsNullOrWhiteSpace(FileBPath) || !File.Exists(FileBPath)))
        {
            MessageBox.Show(
                IsArabic ? "يرجى تحديد مسار ملف الاستشاري (File B) أولاً!" : "Please select Consultant Pricing Schedule (File B) first!",
                IsArabic ? "تنبيه" : "Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _activeOperationCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        try
        {
            var ct = _activeOperationCts.Token;

            // If multiple consultant files exist, run multi-target smart distribution across all packages!
            if (targetFiles.Count > 1)
            {
                await ExecuteMultiTargetReconcileAsync(sourceFilePaths, targetFiles, ct);
                return;
            }

            IsLoading = true;
            ProgressPercentage = 10;
            StatusMessage = IsArabic ? "جارٍ قراءة الملفات ومطابقة البنود بالخوارزميات الذكية..." : "Reconciling line items with intelligent algorithms...";

            string targetPath = targetFiles.Count == 1 ? targetFiles[0].FilePath : FileBPath;

            var result = await _service.ReconcileMultiSourceAsync(
                sourceFilePaths,
                targetPath,
                Sensitivity,
                SheetLinkMappings.Select(m => m.ToModel()).ToList(),
                (ActiveColumnMapping != null && !ActiveColumnMapping.IsAutoDetected) ? ActiveColumnMapping : null,
                ct);

            ct.ThrowIfCancellationRequested();

            _targetResultsMap.Clear();
            _allReconciledPairs.Clear();
            TargetFilterOptions.Clear();

            string singleFileName = Path.GetFileName(targetPath);
            foreach (var p in result.MatchedPairs)
            {
                if (p.InjectedRate.HasValue && !p.IsProvisionalSum && p.Confidence != MatchConfidence.ManualReviewNeeded)
                {
                    p.IsApproved = true;
                }
            }

            _targetResultsMap[singleFileName] = result;
            _allReconciledPairs.AddRange(result.MatchedPairs);

            TargetFilterOptions.Add(singleFileName);
            OnPropertyChanged(nameof(HasMultipleTargets));
            SelectedTargetFilter = singleFileName;
            ApplyTargetFilter();

            Result = result;
            ProgressPercentage = 100;

            // Generate diagnostic report in Log folder
            try
            {
                ReconciliationDiagnosticLogger.WriteDiagnosticReport(targetPath, sourceFilePaths, result);
            }
            catch { }

            StatusMessage = IsArabic 
                ? $"اكتملت المطابقة الذكية في {result.ElapsedTime.TotalSeconds:F2} ثانية ({result.TotalTargetItems:N0} بند عبر {result.TargetSheets.Count} جدول)" 
                : $"Reconciliation complete in {result.ElapsedTime.TotalSeconds:F2}s ({result.TotalTargetItems:N0} items across {result.TargetSheets.Count} tables)";

            SelectedTabIndex = 2; // Jump to Pricing Table tab
        }
        catch (OperationCanceledException)
        {
            StatusMessage = IsArabic ? "تم إلغاء عملية المطابقة بواسطة المستخدم." : "Matching cancelled by user.";
        }
        catch (Exception ex)
        {
            // Log failure diagnostics
            try
            {
                var sources = IngestedFiles
                    .Where(f => f.Role != BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
                    .Select(f => f.FilePath)
                    .ToList();
                if (sources.Count == 0 && File.Exists(FileAPath)) sources.Add(FileAPath);

                ReconciliationDiagnosticLogger.WriteDiagnosticReport(FileBPath, sources, null, exception: ex);
            }
            catch { }

            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show(
                (IsArabic ? "حدث خطأ أثناء المطابقة:\n" : "Error during reconciliation:\n") + ex.Message,
                IsArabic ? "خطأ في المطابقة" : "Matching Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _activeOperationCts?.Dispose();
            _activeOperationCts = null;
            OnPropertyChanged(nameof(CanCancel));
            IsLoading = false;
        }
    }

    private async Task ExecuteReconcileAndExportAsync()
    {
        var targetFiles = IngestedFiles
            .Where(f => f.Role == BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
            .ToList();

        var sourceFiles = IngestedFiles
            .Where(f => f.Role != BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
            .Select(f => f.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceFiles.Count == 0 && File.Exists(FileAPath))
        {
            sourceFiles.Add(FileAPath);
        }

        // If multiple consultant targets are selected, offer unified ZIP package export!
        if (targetFiles.Count > 1 && sourceFiles.Count > 0)
        {
            var zipChoice = MessageBox.Show(
                IsArabic
                    ? $"تم اكتشاف ({targetFiles.Count}) مقايسات استشارية مستهدفة.\n\nهل ترغب في دمج وتصدير كافة المقايسات في حزمة مضغوطة واحدة (ZIP Archive)؟\n\n• اضغط (Yes / نعم): لتصدير كافة المقايسات مدمجة داخل ملف مضغوط ZIP شامل لوحة المؤشرات والتقارير.\n• اضغط (No / لا): لتصدير المقايسات كملفات إكسيل منفصلة في المجلد.\n• اضغط (Cancel / إلغاء): لإلغاء العملية."
                    : $"Multiple ({targetFiles.Count}) consultant schedules detected.\n\nWould you like to export all schedules into a unified ZIP archive?\n\n• Click Yes: Export all into a single ZIP archive.\n• Click No: Export as individual Excel files.\n• Click Cancel: Abort.",
                IsArabic ? "تصدير المقايسات المتعددة" : "Multi-BOQ Export Option",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (zipChoice == MessageBoxResult.Cancel) return;
            if (zipChoice == MessageBoxResult.Yes)
            {
                await ExecuteExportAllZipAsync();
                return;
            }

            await ExecuteBatchReconcileAndExportAsync(sourceFiles, targetFiles.Select(f => f.FilePath).ToList());
            return;
        }

        await ExecuteReconcileAsync();
        if (Result != null && MatchedPairs.Count > 0)
        {
            await ExecuteExportAsync();
        }
    }

    private async Task ExecuteBatchReconcileAndExportAsync(List<string> sourceFiles, List<string> targetFiles)
    {
        _activeOperationCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        try
        {
            var ct = _activeOperationCts.Token;
            IsLoading = true;
            ProgressPercentage = 10;
            StatusMessage = IsArabic 
                ? $"جارٍ بدء الفحص الشامل والتسعير الدفعي لـ ({targetFiles.Count}) مقايسات استشارية..." 
                : $"Starting batch reconciliation across ({targetFiles.Count}) consultant schedules...";

            string exportRoot = GetDefaultExportDirectory();
            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            string batchFolderName = $"Batch_Reconciled_{timeStamp}";
            string outDir = Path.Combine(exportRoot, batchFolderName);
            Directory.CreateDirectory(outDir);

            var batchProgress = new Progress<(int current, int total, string targetName)>(p =>
            {
                ProgressPercentage = (int)((double)p.current / p.total * 90.0);
                StatusMessage = IsArabic 
                    ? $"جارٍ تسعير مقايسة ({p.current}/{p.total}): {p.targetName}..." 
                    : $"Reconciling target ({p.current}/{p.total}): {p.targetName}...";
            });

            var batchResults = await _service.ReconcileBatchMultiTargetAsync(
                sourceFiles,
                targetFiles,
                outDir,
                Sensitivity,
                (ActiveColumnMapping != null && !ActiveColumnMapping.IsAutoDetected) ? ActiveColumnMapping : null,
                batchProgress,
                ct);

            // Copy all contractor priced source files alongside the reconciled files for direct comparison
            var copiedSources = new List<string>();
            foreach (var sPath in sourceFiles)
            {
                if (File.Exists(sPath))
                {
                    string srcCopyName = $"[Source_Priced]_{Path.GetFileName(sPath)}";
                    string srcCopyDest = Path.Combine(outDir, srcCopyName);
                    try
                    {
                        File.Copy(sPath, srcCopyDest, overwrite: true);
                        copiedSources.Add(srcCopyName);
                    }
                    catch { /* non-fatal */ }
                }
            }

            LastExportedFolder = outDir;
            IsExported = true;
            ProgressPercentage = 100;

            // Load all target results into UI for immediate inspection & multi-file navigation
            if (batchResults.Count > 0)
            {
                _targetResultsMap.Clear();
                _allReconciledPairs.Clear();
                TargetFilterOptions.Clear();

                string allBatchOption = IsArabic 
                    ? $"جميع المقايسات المستهدفة ({batchResults.Sum(b => b.TotalItems):N0} بند)" 
                    : $"All Target BOQs ({batchResults.Sum(b => b.TotalItems):N0} items)";
                TargetFilterOptions.Add(allBatchOption);

                foreach (var b in batchResults)
                {
                    _targetResultsMap[b.TargetFileName] = b.Result;
                    _allReconciledPairs.AddRange(b.Result.MatchedPairs);
                    TargetFilterOptions.Add(b.TargetFileName);
                }

                OnPropertyChanged(nameof(HasMultipleTargets));
                SelectedTargetFilter = allBatchOption;
                ApplyTargetFilter();

                var firstValid = batchResults.FirstOrDefault(b => b.TotalItems > 0) ?? batchResults[0];
                FileBPath = firstValid.TargetFilePath;
                OutputFilePath = firstValid.OutputFilePath;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(IsArabic 
                ? $"تم الفحص والتسعير والتصدير الشامل لـ ({batchResults.Count}) مقايسة استشارية بنجاح!\nالمجلد: {outDir}\n" 
                : $"Batch reconciliation complete for ({batchResults.Count}) target schedules!\nFolder: {outDir}\n");

            foreach (var b in batchResults)
            {
                sb.AppendLine($"• {b.TargetFileName}: {b.MatchedItems}/{b.TotalItems} بند مسعر ({b.MatchRate:F1}%) -> {b.OutputFileName}");
            }

            MessageBox.Show(sb.ToString(), IsArabic ? "اكتمل التسعير الدفعي الشامل" : "Batch Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusMessage = IsArabic 
                ? $"تم إنتاج وتصدير {batchResults.Count} ملفات استشارية مسعرة بنجاح." 
                : $"Successfully exported {batchResults.Count} reconciled consultant schedules.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = IsArabic ? "تم إلغاء عملية التسعير الدفعي." : "Batch reconciliation cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = IsArabic ? $"خطأ أثناء التسعير الدفعي: {ex.Message}" : $"Batch reconciliation error: {ex.Message}";
            MessageBox.Show(ex.Message, IsArabic ? "خطأ" : "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void BrowseZipOutput()
    {
        string defaultDir = GetDefaultExportDirectory();
        var dlg = new SaveFileDialog
        {
            Filter = IsArabic ? "ملف أرشيف مضغوط (*.zip)|*.zip" : "ZIP Archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            InitialDirectory = defaultDir,
            FileName = !string.IsNullOrWhiteSpace(ZipOutputFilePath) ? Path.GetFileName(ZipOutputFilePath) : "Consolidated_Reconciled_BOQs.zip",
            Title = IsArabic ? "اختر مسار حفظ حزمة المقايسات المدمجة (ZIP)" : "Select Reconciled ZIP Archive Output Path"
        };

        if (dlg.ShowDialog() == true)
        {
            ZipOutputFilePath = dlg.FileName;
        }
    }

    public void OpenZipFile()
    {
        string path = !string.IsNullOrWhiteSpace(LastExportedZipPath) && File.Exists(LastExportedZipPath)
            ? LastExportedZipPath
            : ZipOutputFilePath;

        if (File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, IsArabic ? "خطأ في فتح الملف" : "Error opening file", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (Directory.Exists(LastExportedFolder))
        {
            OpenOutputFolder();
        }
    }

    public async Task ExecuteExportAllZipAsync()
    {
        var targetFiles = IngestedFiles
            .Where(f => f.Role == BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
            .ToList();

        if (targetFiles.Count == 0 && !string.IsNullOrWhiteSpace(FileBPath) && File.Exists(FileBPath))
        {
            targetFiles.Add(new BoqFileInfo 
            { 
                FilePath = FileBPath, 
                FileName = Path.GetFileName(FileBPath), 
                Role = BoqFileRole.ConsultantTarget 
            });
        }

        if (targetFiles.Count == 0)
        {
            MessageBox.Show(
                IsArabic ? "لا توجد ملفات مقايسة استشارية مستهدفة للتصدير!" : "No target consultant schedules found for export!",
                IsArabic ? "تنبيه" : "Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var sourceFiles = IngestedFiles
            .Where(f => f.Role != BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
            .Select(f => f.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceFiles.Count == 0 && File.Exists(FileAPath))
        {
            sourceFiles.Add(FileAPath);
        }

        if (sourceFiles.Count == 0)
        {
            MessageBox.Show(
                IsArabic ? "يرجى تحديد ملف المقاول (File A) أولاً!" : "Please select Contractor Pricing File (File A) first!",
                IsArabic ? "تنبيه" : "Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Determine destination paths in fixed export folder beside executable
        string exportRoot = GetDefaultExportDirectory();
        string projectPrefix = !string.IsNullOrWhiteSpace(InvoiceOrProjectName) 
            ? InvoiceOrProjectName 
            : Path.GetFileNameWithoutExtension(targetFiles[0].FilePath);
        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        string runFolderName = $"{projectPrefix}_All_Reconciled_Packages_{timeStamp}";

        // Dedicated permanent run directory beside the application
        string targetExportFolder = Path.Combine(exportRoot, runFolderName);
        Directory.CreateDirectory(targetExportFolder);

        // Determine destination ZIP file path
        if (string.IsNullOrWhiteSpace(ZipOutputFilePath) || ZipOutputFilePath.Contains(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)))
        {
            ZipOutputFilePath = Path.Combine(exportRoot, $"{runFolderName}.zip");
        }

        string zipDir = Path.GetDirectoryName(ZipOutputFilePath) ?? exportRoot;
        Directory.CreateDirectory(zipDir);

        _activeOperationCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));

        try
        {
            var ct = _activeOperationCts.Token;
            IsLoading = true;
            ProgressPercentage = 5;
            StatusMessage = IsArabic 
                ? $"جارٍ بدء دمج وتصدير ({targetFiles.Count}) مقايسات استشارية في حزمة ZIP واحدة..." 
                : $"Starting multi-target ZIP package export for ({targetFiles.Count}) BOQs...";

            // Reconcile and export each target workbook directly into targetExportFolder
            var allCombinedPairs = new List<BoqMatchedPair>();
            var exportedWorkbooks = new List<(string TargetName, string OutPath, int Matched, int Total, decimal MatchPct)>();

            for (int i = 0; i < targetFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tFile = targetFiles[i];
                int currentIdx = i + 1;
                ProgressPercentage = 10 + (int)((double)currentIdx / targetFiles.Count * 65.0);
                StatusMessage = IsArabic 
                    ? $"جارٍ دمج وتسعير المقايسة ({currentIdx}/{targetFiles.Count}): {tFile.FileName}..." 
                    : $"Merging and pricing BOQ ({currentIdx}/{targetFiles.Count}): {tFile.FileName}...";

                ReconciliationResult tgtResult;
                if (_targetResultsMap.TryGetValue(tFile.FileName, out var cached))
                {
                    tgtResult = cached;
                }
                else
                {
                    tgtResult = await _service.ReconcileMultiSourceAsync(
                        sourceFiles,
                        tFile.FilePath,
                        Sensitivity,
                        sheetMappings: null,
                        columnMappings: (ActiveColumnMapping != null && !ActiveColumnMapping.IsAutoDetected) ? ActiveColumnMapping : null,
                        ct: ct);
                    _targetResultsMap[tFile.FileName] = tgtResult;
                }

                // Auto-approve valid matched pairs
                foreach (var p in tgtResult.MatchedPairs)
                {
                    if (p.InjectedRate.HasValue && !p.IsProvisionalSum && p.Confidence != MatchConfidence.ManualReviewNeeded)
                    {
                        p.IsApproved = true;
                    }
                }

                allCombinedPairs.AddRange(tgtResult.MatchedPairs);

                string outExcelPath = Path.Combine(targetExportFolder, $"{Path.GetFileNameWithoutExtension(tFile.FilePath)}_Reconciled.xlsx");
                await _service.ExportPricedScheduleAsync(
                    tFile.FilePath,
                    outExcelPath,
                    tgtResult.MatchedPairs,
                    sourceFiles[0],
                    enableDynamicLinking: true,
                    progress: null,
                    ct: ct);

                int pricedCount = tgtResult.MatchedPairs.Count(p => (p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum) || (p.IsApproved && p.Confidence != MatchConfidence.Unmatched && !p.IsVariationOrder));
                int totalCount = tgtResult.MatchedPairs.Count;
                decimal matchRate = totalCount > 0 ? (decimal)pricedCount / totalCount * 100m : 0m;
                exportedWorkbooks.Add((tFile.FileName, Path.GetFileName(outExcelPath), pricedCount, totalCount, matchRate));
            }

            ct.ThrowIfCancellationRequested();
            ProgressPercentage = 80;
            StatusMessage = IsArabic ? "جارٍ إنشاء لوحة المؤشرات الشاملة لجميع المقايسات..." : "Creating Master Commercial Dashboard...";

            // Create Master Standalone Commercial Dashboard inside the export folder
            string dashboardPath = Path.Combine(targetExportFolder, "Master_Executive_Commercial_Dashboard.xlsx");
            await Task.Run(() =>
            {
                ClosedXmlExporter.ExportStandaloneDashboard(dashboardPath, allCombinedPairs, sourceFiles[0]);
            }, ct);

            // Copy all contractor priced source files directly into targetExportFolder alongside the reconciled files
            var copiedSourcesList = new List<string>();
            foreach (var sPath in sourceFiles)
            {
                if (File.Exists(sPath))
                {
                    string srcCopyName = $"[Source_Priced]_{Path.GetFileName(sPath)}";
                    string srcCopyDest = Path.Combine(targetExportFolder, srcCopyName);
                    try
                    {
                        File.Copy(sPath, srcCopyDest, overwrite: true);
                        copiedSourcesList.Add(srcCopyName);
                    }
                    catch { /* non-fatal */ }
                }
            }

            // Create Technical Audit & Summary Report file inside the export folder
            string reportPath = Path.Combine(targetExportFolder, "Reconciliation_Audit_Summary.txt");
            var sbReport = new System.Text.StringBuilder();
            sbReport.AppendLine("==========================================================================");
            sbReport.AppendLine("           SMARTBOQ ENTERPRISE - BATCH RECONCILIATION SUMMARY             ");
            sbReport.AppendLine("==========================================================================");
            sbReport.AppendLine($"Export Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sbReport.AppendLine($"Export Directory: {targetExportFolder}");
            sbReport.AppendLine($"Total Consultant Schedules: {targetFiles.Count}");
            sbReport.AppendLine($"Total Line Items: {allCombinedPairs.Count:N0}");
            sbReport.AppendLine($"Total Priced Items: {allCombinedPairs.Count(p => (p.InjectedRate > 0 && !p.IsProvisionalSum) || (p.IsApproved && p.Confidence != MatchConfidence.Unmatched && !p.IsVariationOrder)):N0}");
            sbReport.AppendLine();
            sbReport.AppendLine("PRICING SOURCE FILES (Copied to this folder for direct audit & comparison):");
            foreach (var s in sourceFiles) sbReport.AppendLine($" • [Source_Priced]_{Path.GetFileName(s)} (Original: {s})");
            sbReport.AppendLine();
            sbReport.AppendLine("SCHEDULES BREAKDOWN:");
            foreach (var w in exportedWorkbooks)
            {
                sbReport.AppendLine($" • {w.TargetName} -> {w.OutPath} | {w.Matched}/{w.Total} items priced ({w.MatchPct:F1}%)");
            }
            sbReport.AppendLine("==========================================================================");
            File.WriteAllText(reportPath, sbReport.ToString(), System.Text.Encoding.UTF8);

            ct.ThrowIfCancellationRequested();
            ProgressPercentage = 90;
            StatusMessage = IsArabic ? "جارٍ ضغط وأرشفة كافة الملفات في حزمة ZIP واحدة..." : "Compressing all files into single ZIP archive...";

            // Ensure destination zip file is ready
            if (File.Exists(ZipOutputFilePath))
            {
                File.Delete(ZipOutputFilePath);
            }

            // Create ZIP archive containing all reconciled files, copied source files, and reports
            await Task.Run(() =>
            {
                ZipFile.CreateFromDirectory(targetExportFolder, ZipOutputFilePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            }, ct);

            ProgressPercentage = 100;
            LastExportedZipPath = ZipOutputFilePath;
            LastExportedFolder = targetExportFolder;
            IsZipExported = true;
            IsExported = true;

            var sbSuccess = new System.Text.StringBuilder();
            sbSuccess.AppendLine(IsArabic 
                ? $"تم بنجاح دمج وتصدير كافة المقايسات ({targetFiles.Count} مقايسة) وحفظها في المجلد الثابت بجانب البرنامج!" 
                : $"Successfully merged and exported all ({targetFiles.Count}) BOQs into the fixed export directory!");
            sbSuccess.AppendLine();
            sbSuccess.AppendLine(IsArabic ? $"مجلد الإخراج الثابت:" : "Output Folder:");
            sbSuccess.AppendLine(targetExportFolder);
            sbSuccess.AppendLine();
            sbSuccess.AppendLine(IsArabic ? $"ملف الأرشيف المضغوط (ZIP):" : "ZIP Archive Path:");
            sbSuccess.AppendLine(ZipOutputFilePath);
            sbSuccess.AppendLine();
            sbSuccess.AppendLine(IsArabic ? "محتويات المجلد والأرشيف:" : "Folder & Archive Contents:");
            sbSuccess.AppendLine(IsArabic 
                ? $"• ({targetFiles.Count}) ملفات مقايسات إكسيل مسعرة بالكامل بنسبة 100% حفاظ على المعادلات." 
                : $"• ({targetFiles.Count}) fully priced consultant Excel workbooks.");
            sbSuccess.AppendLine(IsArabic 
                ? $"• ({sourceFiles.Count}) ملفات الأسعار الأصلية المسحوب منها الفئات ([Source_Priced]) للمقارنة الفورية." 
                : $"• ({sourceFiles.Count}) source priced workbooks copied for direct side-by-side comparison.");
            sbSuccess.AppendLine(IsArabic 
                ? "• ملف لوحة المؤشرات الشاملة (Master_Executive_Commercial_Dashboard.xlsx)." 
                : "• Master Executive Commercial Dashboard.");
            sbSuccess.AppendLine(IsArabic 
                ? "• تقرير الفحص والتدقيق الفني الشامل (Reconciliation_Audit_Summary.txt)." 
                : "• Technical audit summary text report.");

            StatusMessage = IsArabic 
                ? $"تم إنشاء مجلد التصدير وحزمة ZIP المجمعة بنجاح ({targetFiles.Count} مقايسة)." 
                : $"Unified export folder and ZIP package created successfully ({targetFiles.Count} BOQs).";

            var resultMsg = MessageBox.Show(
                sbSuccess.ToString() + "\n\n" + (IsArabic ? "هل ترغب في فتح مجلد التصدير الآن لمراجعة ومقارنة الملفات؟" : "Would you like to open the export folder now to compare and review files?"),
                IsArabic ? "اكتمل دمج وتصدير الحزمة الشاملة" : "Batch ZIP Export Complete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (resultMsg == MessageBoxResult.Yes)
            {
                OpenOutputFolder();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = IsArabic ? "تم إلغاء عملية التصدير المضغوطة." : "ZIP export cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ في تصدير ZIP: {ex.Message}";
            MessageBox.Show(ex.Message, IsArabic ? "خطأ في تصدير ZIP" : "ZIP Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _activeOperationCts?.Dispose();
            _activeOperationCts = null;
            OnPropertyChanged(nameof(CanCancel));
            IsLoading = false;
        }
    }

    private async Task ExecuteExportAsync()
    {
        // If user is currently on the aggregated "All Target BOQs" view with multiple targets, prompt for ZIP package
        if (HasMultipleTargets && (SelectedTargetFilter == null || SelectedTargetFilter.StartsWith("جميع المقايسات") || SelectedTargetFilter.StartsWith("All Target")))
        {
            var zipChoice = MessageBox.Show(
                IsArabic
                    ? "أنت تعرض حالياً كافة المقايسات المستهدفة مجمعة.\n\nهل ترغب في دمج وتصدير جميع المقايسات في حزمة مضغوطة واحدة (ZIP Archive)؟\n\n• اضغط (Yes / نعم): لتصدير كافة المقايسات في ملف مضغوط ZIP مجمع شامل الداشبورد والتقارير.\n• اضغط (No / لا): لتصدير المقايسة المحددة حالياً فقط كملف Excel منفرد.\n• اضغط (Cancel / إلغاء): لإلغاء العملية."
                    : "You are currently viewing all target schedules.\n\nWould you like to export all schedules into a unified ZIP archive?\n\n• Click Yes: Export into a single ZIP archive.\n• Click No: Export only the active schedule as a single Excel file.\n• Click Cancel: Abort.",
                IsArabic ? "خيارات التصدير الشامل" : "Consolidated Export Options",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (zipChoice == MessageBoxResult.Cancel) return;
            if (zipChoice == MessageBoxResult.Yes)
            {
                await ExecuteExportAllZipAsync();
                return;
            }
        }

        if (Result == null || MatchedPairs.Count == 0)
        {
            if (!File.Exists(FileAPath) || !File.Exists(FileBPath))
            {
                MessageBox.Show(
                    IsArabic ? "يرجى تحديد ملف المقاول (File A) وملف الاستشاري (File B) أولاً!" : "Please select File A and File B first!",
                    IsArabic ? "تنبيه" : "Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            await ExecuteReconcileAsync();
            if (Result == null || MatchedPairs.Count == 0) return;
        }

        _activeOperationCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        ThrottledProgress<int>? progress = null;
        try
        {
            var ct = _activeOperationCts.Token;
            IsLoading = true;
            ProgressPercentage = 10;
            progress = new ThrottledProgress<int>(pct => ProgressPercentage = (int)(pct * 0.7), throttleIntervalMs: 50);
            StatusMessage = IsArabic ? "جارٍ إعداد وتجهيز ملفات الدمج والتصدير..." : "Preparing merge & export files...";

            UpdateDefaultOutputPath();
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                string dir = GetDefaultExportDirectory();
                string baseName = Path.GetFileNameWithoutExtension(FileBPath);
                OutputFilePath = Path.Combine(dir, $"{baseName}_Reconciled.xlsx");
            }

            string outDir = Path.GetDirectoryName(OutputFilePath) ?? GetDefaultExportDirectory();
            Directory.CreateDirectory(outDir);
            string baseNameB = !string.IsNullOrWhiteSpace(FileBPath) ? Path.GetFileNameWithoutExtension(FileBPath) : "BOQ_Project";
            string dashboardPath = Path.Combine(outDir, $"{baseNameB}_Executive_Dashboard.xlsx");
            DashboardFilePath = dashboardPath;

            // Intelligent pre-flight check for open / locked files in Excel or WPS
            if (IsFileLocked(OutputFilePath))
            {
                string nextAvailable = GetNextAvailableNumberedPath(OutputFilePath);
                string candidateName = Path.GetFileName(nextAvailable);

                var prompt = MessageBox.Show(
                    IsArabic
                        ? $"الملف مفتوح حالياً في برنامج آخر (Excel أو WPS):\n{Path.GetFileName(OutputFilePath)}\n\nهل ترغب في حفظه تلقائياً باسم إصدار جديد؟\n[{candidateName}]\n\n• اضغط (Yes / نعم): للحفظ كإصدار جديد فوراً دون إغلاق Excel.\n• اضغط (No / لا): إذا أغلقت الملف في Excel وتريد استبدال الملف الحالي.\n• اضغط (Cancel / إلغاء): لإلغاء التصدير."
                        : $"The target file is open in Excel or WPS:\n{Path.GetFileName(OutputFilePath)}\n\nWould you like to save it as a new version automatically?\n[{candidateName}]\n\n• Click Yes: Save as new version now.\n• Click No: Retry overwriting after closing Excel.\n• Click Cancel: Abort export.",
                    IsArabic ? "الملف قيد الاستخدام - خيارات الحفظ الذكي" : "File In Use - Smart Save",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (prompt == MessageBoxResult.Cancel)
                {
                    StatusMessage = IsArabic ? "تم إلغاء التصدير." : "Export cancelled.";
                    return;
                }
                else if (prompt == MessageBoxResult.Yes)
                {
                    OutputFilePath = nextAvailable;
                }
                else
                {
                    // User chose No: they closed Excel and want to overwrite.
                    // If STILL locked, auto-switch to next available to prevent failure
                    if (IsFileLocked(OutputFilePath))
                    {
                        OutputFilePath = nextAvailable;
                    }
                }
            }

            if (IsFileLocked(dashboardPath))
            {
                dashboardPath = GetNextAvailableNumberedPath(dashboardPath);
                DashboardFilePath = dashboardPath;
            }

            ct.ThrowIfCancellationRequested();
            StatusMessage = IsArabic ? "جارٍ حقن الأسعار وإنشاء شيتات التدقيق والربط..." : "Injecting rates and building audit sheets...";

            // Automatically ensure all valid matched pairs are approved for injection
            foreach (var p in MatchedPairs)
            {
                if (p.InjectedRate.HasValue && !p.IsProvisionalSum && p.Confidence != MatchConfidence.ManualReviewNeeded)
                {
                    p.IsApproved = true;
                }
            }

            // 1. Export reconciled schedule with audit report and linkage map
            await _service.ExportPricedScheduleAsync(
                FileBPath, 
                OutputFilePath, 
                MatchedPairs.ToList(), 
                FileAPath, 
                enableDynamicLinking: true, 
                progress,
                ct);

            ct.ThrowIfCancellationRequested();
            ProgressPercentage = 75;
            StatusMessage = IsArabic ? "جارٍ إنشاء لوحة المؤشرات المستقلة (Dashboard)..." : "Generating standalone Executive Dashboard...";

            // 2. Export the Standalone Executive Dashboard
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                ClosedXmlExporter.ExportStandaloneDashboard(dashboardPath, MatchedPairs.ToList(), FileAPath);
            }, ct);

            ct.ThrowIfCancellationRequested();
            ProgressPercentage = 90;
            StatusMessage = IsArabic ? "جارٍ أرشفة السجل التاريخي..." : "Archiving snapshot to SQLite...";

            // 3. Persist snapshot to local SQLite repository
            string invoiceOrProj = !string.IsNullOrWhiteSpace(InvoiceOrProjectName) 
                ? InvoiceOrProjectName 
                : baseNameB;

            await _service.SaveSnapshotAsync(
                invoiceOrProj,
                $"Rev-{DateTime.Now:yyyyMMdd-HHmm}",
                MatchedPairs.ToList(),
                invoiceName: invoiceOrProj,
                sourceFileName: !string.IsNullOrWhiteSpace(FileAPath) ? Path.GetFileName(FileAPath) : string.Empty,
                targetFileName: !string.IsNullOrWhiteSpace(FileBPath) ? Path.GetFileName(FileBPath) : string.Empty,
                exportFilePath: OutputFilePath,
                ct: ct);

            _ = ExecuteHistoricalSearchAsync(HistoricalSearchQuery);

            string diagLogPath = string.Empty;
            try
            {
                diagLogPath = ReconciliationDiagnosticLogger.WriteDiagnosticReport(
                    FileBPath,
                    new[] { FileAPath },
                    Result,
                    exportOutputPath: OutputFilePath,
                    dashboardOutputPath: dashboardPath,
                    snapshotRevisionId: $"Rev-{DateTime.Now:yyyyMMdd-HHmm}");
            }
            catch { }

            // Copy contractor priced source file(s) alongside the output file so user can directly compare
            var sourcePaths = IngestedFiles
                .Where(f => f.Role != BoqFileRole.ConsultantTarget && File.Exists(f.FilePath))
                .Select(f => f.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sourcePaths.Count == 0 && !string.IsNullOrWhiteSpace(FileAPath) && File.Exists(FileAPath))
            {
                sourcePaths.Add(FileAPath);
            }

            var copiedSources = new List<string>();
            foreach (var srcPath in sourcePaths)
            {
                try
                {
                    string srcName = Path.GetFileName(srcPath);
                    string copyDest = Path.Combine(outDir, $"[Source_Priced]_{srcName}");
                    if (!string.Equals(srcPath, copyDest, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(srcPath, copyDest, overwrite: true);
                        copiedSources.Add(Path.GetFileName(copyDest));
                    }
                }
                catch { /* non-fatal */ }
            }

            ProgressPercentage = 100;
            IsExported = true;
            LastExportedSchedulePath = OutputFilePath;
            LastExportedDashboardPath = dashboardPath;
            LastExportedFolder = outDir;

            StatusMessage = IsArabic 
                ? "تم دمج وتصدير ملف المقايسة والداش بورد وسجلات الربط بنجاح!" 
                : "Export completed and snapshot archived successfully!";

            string logHint = !string.IsNullOrWhiteSpace(diagLogPath)
                ? $"\n3. تقرير التشخيص الذكي (Log):\n{diagLogPath}\n"
                : string.Empty;

            string sourcesHint = copiedSources.Count > 0
                ? (IsArabic ? $"\n4. ملفات الأسعار الأصلية (تم نسخها للمقارنة المباشرة):\n{string.Join("\n", copiedSources.Select(p => $" • {p}"))}\n"
                            : $"\n4. Source Priced Files (Copied alongside for comparison):\n{string.Join("\n", copiedSources.Select(p => $" • {p}"))}\n")
                : string.Empty;

            // Prompt user with options to open files
            string msg = IsArabic
                ? $"تم الدمج والتصدير بنجاح!\n\n" +
                  $"1. ملف المقايسة المدمج:\n{OutputFilePath}\n" +
                  $"(يتضمن أوراق العمل المسعرة + سجل التدقيق Audit_Report + خريطة ربط الأسعار Pricing_Linkage_Map)\n\n" +
                  $"2. لوحة مؤشرات الإدارة (Dashboard):\n{dashboardPath}\n" +
                  sourcesHint +
                  logHint +
                  $"\nهل تريد فتح ملف المقايسة المسعر الآن في Excel؟"
                : $"Export completed successfully!\n\nReconciled Schedule:\n{OutputFilePath}\n\nExecutive Dashboard:\n{dashboardPath}\n" +
                  sourcesHint +
                  logHint +
                  $"\nDo you want to open the reconciled file now in Excel?";

            var res = MessageBox.Show(msg, IsArabic ? "اكتمل التصدير بنجاح" : "Export Success", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (res == MessageBoxResult.Yes)
            {
                OpenPricedFile();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = IsArabic ? "تم إلغاء عملية التصدير بواسطة المستخدم." : "Export cancelled by user.";
        }
        catch (IOException ioEx)
        {
            try
            {
                // Graceful automated auto-version recovery
                string autoRecoverPath = GetNextAvailableNumberedPath(OutputFilePath);
                OutputFilePath = autoRecoverPath;

                await _service.ExportPricedScheduleAsync(
                    FileBPath, 
                    OutputFilePath, 
                    MatchedPairs.ToList(), 
                    FileAPath, 
                    enableDynamicLinking: true, 
                    progress);

                LastExportedSchedulePath = OutputFilePath;
                StatusMessage = IsArabic ? $"تم الحفظ بنجاح كإصدار جديد ({Path.GetFileName(OutputFilePath)})" : "Saved as new revision.";
                MessageBox.Show(
                    IsArabic
                        ? $"نظراً لأن الملف الأصلي كان قيد الاستخدام في Excel، فقد تم حفظ النسخة المدمجة بنجاح باسم جديد:\n{OutputFilePath}"
                        : $"Because the original file was in use, it was saved successfully as:\n{OutputFilePath}",
                    IsArabic ? "تم الحفظ بنجاح كإصدار جديد" : "Export Success (New Revision)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                OpenPricedFile();
            }
            catch
            {
                StatusMessage = IsArabic ? "تنبيه: الملف مفتوح حالياً في برنامج آخر (Excel أو WPS)" : $"File locked: {ioEx.Message}";
                MessageBox.Show(
                    IsArabic 
                        ? $"تعذر حفظ الملف لأن الملف مفتوح حالياً في برنامج Excel أو WPS:\n{OutputFilePath}\n\nيرجى إغلاق برنامج Excel أو WPS ثم إعادة الضغط على تصدير."
                        : $"The file is currently open in Excel or WPS:\n{OutputFilePath}\n\nPlease close the spreadsheet application and try again.",
                    IsArabic ? "الملف قيد الاستخدام" : "File In Use",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            try
            {
                ReconciliationDiagnosticLogger.WriteDiagnosticReport(
                    FileBPath,
                    new[] { FileAPath },
                    Result,
                    exportOutputPath: OutputFilePath,
                    dashboardOutputPath: DashboardFilePath,
                    exception: ex);
            }
            catch { }

            StatusMessage = $"Export Error: {ex.Message}";
            MessageBox.Show(
                (IsArabic ? "حدث خطأ أثناء التصدير:\n" : "Error during export:\n") + ex.Message,
                IsArabic ? "خطأ في التصدير" : "Export Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _activeOperationCts?.Dispose();
            _activeOperationCts = null;
            OnPropertyChanged(nameof(CanCancel));
            IsLoading = false;
        }
    }

    private void OpenPricedFile()
    {
        string path = !string.IsNullOrWhiteSpace(LastExportedSchedulePath) && File.Exists(LastExportedSchedulePath)
            ? LastExportedSchedulePath
            : OutputFilePath;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void OpenDashboard()
    {
        string path = !string.IsNullOrWhiteSpace(LastExportedDashboardPath) && File.Exists(LastExportedDashboardPath)
            ? LastExportedDashboardPath
            : DashboardFilePath;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void OpenOutputFolder()
    {
        string dir = !string.IsNullOrWhiteSpace(LastExportedFolder) && Directory.Exists(LastExportedFolder)
            ? LastExportedFolder
            : (!string.IsNullOrWhiteSpace(OutputFilePath) ? Path.GetDirectoryName(OutputFilePath) ?? "" : "");

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            dir = GetDefaultExportDirectory();
        }

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
    }

    private static bool IsFileLocked(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetNextAvailableNumberedPath(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return originalPath;
        string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string fileNameNoExt = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);

        // Pattern: _v(\d+) or _Rev(\d+) or _(\d+)
        var match = Regex.Match(fileNameNoExt, @"^(.*?)(?:_v|_Rev|_)(\d+)$", RegexOptions.IgnoreCase);
        string baseName = match.Success ? match.Groups[1].Value : fileNameNoExt;
        int version = match.Success && int.TryParse(match.Groups[2].Value, out int v) ? v + 1 : 2;

        while (true)
        {
            string candidate = Path.Combine(dir, $"{baseName}_v{version}{ext}");
            if (!File.Exists(candidate) || !IsFileLocked(candidate))
            {
                return candidate;
            }
            version++;
        }
    }

    private void ToggleLanguage()
    {
        string newCulture = IsArabic ? "en-US" : "ar-EG";
        _loc.SetCulture(newCulture);
        UiFlowDirection = IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        // Refresh all localized properties
        OnPropertyChanged(nameof(CurrentLanguageCode));
        OnPropertyChanged(nameof(IsArabic));
        OnPropertyChanged(nameof(UiFlowDirection));
        OnPropertyChanged(nameof(TabSummary));
        OnPropertyChanged(nameof(TabCompare));
        OnPropertyChanged(nameof(TabPricingGrid));
        OnPropertyChanged(nameof(TabProjectRates));
        OnPropertyChanged(nameof(TabExport));
        OnPropertyChanged(nameof(LblSearchAccuracy));
        OnPropertyChanged(nameof(LblCalculationMethod));
        OnPropertyChanged(nameof(LblOfflineBadge));
        OnPropertyChanged(nameof(VerificationSummaryText));
        OnPropertyChanged(nameof(ThemeText));
        OnPropertyChanged(nameof(CurrentPageDisplay));
        OnPropertyChanged(nameof(PageInfoText));
    }

    private void ToggleTheme()
    {
        ThemeManager.ApplyTheme(!ThemeManager.IsDarkMode);
        OnPropertyChanged(nameof(IsDarkMode));
        OnPropertyChanged(nameof(ThemeIcon));
        OnPropertyChanged(nameof(ThemeText));
    }

    private void SetFilter(string filter)
    {
        ActiveFilter = filter;
    }

    private void ApproveAll()
    {
        foreach (var p in MatchedPairs)
        {
            if (p.InjectedRate.HasValue && !p.TargetItem.IsProtected)
            {
                p.IsApproved = true;
            }
        }
        FilteredItems.Refresh();
        UpdatePagination();
    }

    public void UpdatePagination(bool resetToPageOne = false)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        string query = SearchQuery?.Trim() ?? string.Empty;
        string filter = ActiveFilter ?? "All";
        int pageSize = PageSize > 0 ? PageSize : 100;
        int reqPage = resetToPageOne ? 1 : CurrentPage;

        // Fast path for unfiltered state
        if (string.IsNullOrEmpty(query) && filter == "All")
        {
            int total = MatchedPairs.Count;
            TotalFilteredCount = total;
            TotalPages = Math.Max(1, (int)Math.Ceiling((double)total / pageSize));
            CurrentPage = Math.Clamp(reqPage, 1, TotalPages);
            JumpPageInput = CurrentPage.ToString();

            PagedItems.Clear();
            var fastSlice = MatchedPairs.Skip((CurrentPage - 1) * pageSize).Take(pageSize);
            foreach (var item in fastSlice)
            {
                PagedItems.Add(item);
            }
            NotifyPaginationProperties();
            return;
        }

        // Parallel search evaluation
        var pairsSnapshot = MatchedPairs.ToList();

        Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;

            var parallelFiltered = pairsSnapshot
                .AsParallel()
                .WithCancellation(ct)
                .WithDegreeOfParallelism(Environment.ProcessorCount)
                .Where(pair =>
                {
                    // 1. Text filter
                    if (!string.IsNullOrEmpty(query))
                    {
                        bool match = pair.TargetItem.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     pair.TargetItem.ItemCode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     pair.TargetItem.BillNumber.Contains(query, StringComparison.OrdinalIgnoreCase);
                        if (!match) return false;
                    }

                    // 2. Category filter
                    return filter switch
                    {
                        "Priced" => pair.InjectedRate.HasValue && pair.InjectedRate > 0 && !pair.IsProvisionalSum,
                        "VO" => pair.IsVariationOrder,
                        "PS" => pair.IsProvisionalSum,
                        "Review" => pair.Confidence == MatchConfidence.ManualReviewNeeded,
                        "Approved" => pair.IsApproved,
                        _ => true
                    };
                })
                .ToList();

            if (ct.IsCancellationRequested) return;

            int total = parallelFiltered.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)total / pageSize));
            int page = Math.Clamp(reqPage, 1, totalPages);
            var pageSlice = parallelFiltered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;

                TotalFilteredCount = total;
                TotalPages = totalPages;
                CurrentPage = page;
                JumpPageInput = page.ToString();

                PagedItems.Clear();
                foreach (var item in pageSlice)
                {
                    PagedItems.Add(item);
                }
                NotifyPaginationProperties();
            });
        }, ct);
    }

    private void NotifyPaginationProperties()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(TotalFilteredCount));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(CurrentPageDisplay));
        OnPropertyChanged(nameof(PageInfoText));
        OnPropertyChanged(nameof(JumpPageInput));
    }

    #endregion

    #region Historical Rates & Excel Navigation

    private void DebounceHistoricalSearch()
    {
        _historicalSearchCts?.Cancel();
        _historicalSearchCts = new CancellationTokenSource();
        var token = _historicalSearchCts.Token;

        Task.Delay(250, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _ = ExecuteHistoricalSearchAsync(HistoricalSearchQuery);
                });
            }
        }, TaskScheduler.Default);
    }

    public async Task ExecuteHistoricalSearchAsync(string? query = null)
    {
        IsSearchingHistorical = true;
        try
        {
            var results = await _service.SearchHistoricalRatesAsync(query);
            HistoricalSearchResults.Clear();
            foreach (var item in results)
            {
                HistoricalSearchResults.Add(item);
            }
            OnPropertyChanged(nameof(HistoricalTotalCount));
            OnPropertyChanged(nameof(HasNoHistoricalResults));
            OnPropertyChanged(nameof(HasHistoricalResults));
            OnPropertyChanged(nameof(HistoricalAvgRate));
            OnPropertyChanged(nameof(HistoricalLatestDate));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Historical Search Error: {ex.Message}";
        }
        finally
        {
            IsSearchingHistorical = false;
            OnPropertyChanged(nameof(HasNoHistoricalResults));
        }
    }

    public async Task LoadHistoricalRatesIfEmptyAsync()
    {
        if (HistoricalSearchResults.Count == 0)
        {
            await ExecuteHistoricalSearchAsync(HistoricalSearchQuery);
        }
    }

    public void OpenSheetLocation(BoqSheetSummary? sheet)
    {
        if (sheet == null) return;
        string? filePath = SelectedFileForDetails?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            var file = IngestedFiles.FirstOrDefault(f => f.Sheets.Any(s => s.SheetName == sheet.SheetName));
            filePath = file?.FilePath;
        }
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            ExcelNavigator.OpenWorkbookAtSheet(filePath, sheet.SheetName, sheet.StartRowIndex);
        }
    }

    public void OpenLinkedSheetLocation(SheetLinkMappingViewModel? mapping)
    {
        if (mapping == null) return;
        if (!string.IsNullOrWhiteSpace(FileBPath) && File.Exists(FileBPath))
        {
            ExcelNavigator.OpenWorkbookAtSheet(FileBPath, mapping.TargetSheetName, mapping.TargetStartRow);
        }
        else if (IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget) is { } targetFile)
        {
            ExcelNavigator.OpenWorkbookAtSheet(targetFile.FilePath, mapping.TargetSheetName, mapping.TargetStartRow);
        }
    }

    public void OpenLinkedSourceSheetLocation(SheetLinkMappingViewModel? mapping)
    {
        if (mapping == null) return;
        string? sheetName = mapping.SelectedSourceSheet;
        if (!string.IsNullOrWhiteSpace(sheetName) && sheetName.StartsWith("[") && sheetName.EndsWith("]"))
        {
            // Unscoped match: open source file at active sheet
            sheetName = null;
        }
        else if (!string.IsNullOrWhiteSpace(sheetName) && sheetName.StartsWith("[") && sheetName.Contains("] "))
        {
            sheetName = sheetName.Substring(sheetName.IndexOf("] ") + 2).Trim();
        }

        string? filePath = !string.IsNullOrWhiteSpace(FileAPath) && File.Exists(FileAPath)
            ? FileAPath
            : IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced)?.FilePath;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            filePath = IngestedFiles.FirstOrDefault(f => f.Role != BoqFileRole.ConsultantTarget)?.FilePath;
        }

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            ExcelNavigator.OpenWorkbookAtSheet(filePath, sheetName, mapping.SourceStartRow);
        }
    }

    public void OpenMatchedSourceLocation(BoqMatchedPair? pair)
    {
        if (pair?.MatchedSourceItem == null) return;
        string? filePath = !string.IsNullOrWhiteSpace(FileAPath) && File.Exists(FileAPath)
            ? FileAPath
            : IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced)?.FilePath;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            filePath = IngestedFiles.FirstOrDefault(f => f.Role != BoqFileRole.ConsultantTarget)?.FilePath;
        }

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            string? sheetOrBill = !string.IsNullOrWhiteSpace(pair.MatchedSourceItem.BillNumber)
                ? pair.MatchedSourceItem.BillNumber
                : pair.MatchedSourceItem.SheetName;
            ExcelNavigator.OpenWorkbookAtSheet(filePath, sheetOrBill, pair.MatchedSourceItem.AnchorRowIndex);
        }
    }

    public void OpenMatchedTargetLocation(BoqMatchedPair? pair)
    {
        if (pair == null) return;
        if (!string.IsNullOrWhiteSpace(FileBPath) && File.Exists(FileBPath))
        {
            ExcelNavigator.OpenWorkbookAtSheet(FileBPath, pair.TargetItem.SheetName, pair.TargetItem.AnchorRowIndex);
        }
    }

    public void OpenHistoricalItemLocation(HistoricalRateItem? item)
    {
        if (item == null) return;
        string? targetPath = null;
        if (!string.IsNullOrWhiteSpace(item.ExportFilePath) && File.Exists(item.ExportFilePath))
        {
            targetPath = item.ExportFilePath;
        }
        else if (!string.IsNullOrWhiteSpace(FileBPath) && File.Exists(FileBPath))
        {
            targetPath = FileBPath;
        }
        else if (!string.IsNullOrWhiteSpace(FileAPath) && File.Exists(FileAPath))
        {
            targetPath = FileAPath;
        }

        if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
        {
            ExcelNavigator.OpenWorkbookAtSheet(targetPath, item.BillNumber);
        }
    }

    public void OpenDatabaseLocation()
    {
        try
        {
            if (File.Exists(DatabaseFilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{DatabaseFilePath}\"");
            }
            else
            {
                string dir = Path.GetDirectoryName(DatabaseFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        }
        catch { }
    }

    #endregion

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        return SetProperty(ref field, value, propertyName);
    }
}
