using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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

namespace SmartBOQ.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly BoqReconciliationService _service;
    private readonly ILocalizationService _loc;

    private string _fileAPath = string.Empty;
    private string _fileBPath = string.Empty;
    private string _outputFilePath = string.Empty;
    private string _dashboardFilePath = string.Empty;

    private bool _isExported;
    private string _lastExportedSchedulePath = string.Empty;
    private string _lastExportedDashboardPath = string.Empty;
    private string _lastExportedFolder = string.Empty;

    private bool _isLoading;
    private int _progressPercentage;
    private string _statusMessage = "Ready";
    private string _searchQuery = string.Empty;
    private string _activeFilter = "All";
    private double _sensitivity = 0.85;
    private FlowDirection _uiFlowDirection = FlowDirection.RightToLeft;
    private int _selectedTabIndex = 0;

    private VerificationReport? _verificationReport;
    private ReconciliationResult? _reconciliationResult;

    public ObservableCollection<BoqMatchedPair> MatchedPairs { get; } = [];
    public ICollectionView FilteredItems { get; }


    public MainViewModel()
    {
        _loc = new LocalizationService();
        _loc.SetCulture("ar-EG");

        string dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartBOQ", "smartboq.db");
        _service = new BoqReconciliationService(
            new PreFlightVerificationGate(),
            new HatchwayFlatReader(),
            new HierarchicalBoqReader(),
            new HybridWeightedMatcher(),
            new ClosedXmlExporter(),
            new SqliteBoqRepository(dbPath)
        );

        FilteredItems = CollectionViewSource.GetDefaultView(MatchedPairs);
        FilteredItems.Filter = FilterItemPredicate;

        // Initialize Commands
        BrowseFileACommand = new RelayCommand(BrowseFileA);
        BrowseFileBCommand = new RelayCommand(BrowseFileB);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        VerifyCommand = new AsyncRelayCommand(ExecuteVerifyAsync);
        ReconcileCommand = new AsyncRelayCommand(ExecuteReconcileAsync);
        ReconcileAndExportCommand = new AsyncRelayCommand(ExecuteReconcileAndExportAsync);
        ExportCommand = new AsyncRelayCommand(ExecuteExportAsync);
        OpenReconciledFileCommand = new RelayCommand(OpenPricedFile);
        OpenDashboardCommand = new RelayCommand(OpenDashboard);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        SetFilterCommand = new RelayCommand(p => SetFilter(p?.ToString() ?? "All"));
        ApproveAllCommand = new RelayCommand(ApproveAll);

        // Auto-detect default workspace files if present
        InitializeDefaultFiles();
    }

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

    public bool IsLoading { get => _isLoading; set => SetField(ref _isLoading, value); }
    public int ProgressPercentage { get => _progressPercentage; set => SetField(ref _progressPercentage, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public double Sensitivity { get => _sensitivity; set => SetField(ref _sensitivity, value); }
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetField(ref _selectedTabIndex, value); }

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
            }
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

    // Localized UI Texts (Dynamic 1-2 words)
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
    public RelayCommand OpenReconciledFileCommand { get; }
    public RelayCommand OpenDashboardCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand ApproveAllCommand { get; }

    #endregion

    #region Command Handlers

    private void InitializeDefaultFiles()
    {
        try
        {
            string defaultA = Path.GetFullPath("DP3 - Hatchway.xlsx");
            if (File.Exists(defaultA))
            {
                FileAPath = defaultA;
            }

            string defaultB = Path.GetFullPath("REH.1.xlsx");
            if (!File.Exists(defaultB))
            {
                defaultB = Path.GetFullPath("REH.08.26.3199 DP3 Pricing Schedule Re-Measure.xlsx");
            }
            if (File.Exists(defaultB))
            {
                FileBPath = defaultB;
            }
        }
        catch
        {
            // Ignore path discovery exceptions
        }
    }

    private void UpdateDefaultOutputPath()
    {
        if (string.IsNullOrWhiteSpace(_fileBPath)) return;

        string dir = Path.GetDirectoryName(_fileBPath) ?? string.Empty;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(_fileBPath);
        if (nameWithoutExt.EndsWith("_Reconciled", StringComparison.OrdinalIgnoreCase))
        {
            nameWithoutExt = nameWithoutExt[..^11];
        }

        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            OutputFilePath = Path.Combine(dir, $"{nameWithoutExt}_Reconciled.xlsx");
        }

        string targetDir = !string.IsNullOrWhiteSpace(OutputFilePath) 
            ? (Path.GetDirectoryName(OutputFilePath) ?? dir) 
            : dir;
        DashboardFilePath = Path.Combine(targetDir, "DP3_Executive_Dashboard.xlsx");
    }

    private void BrowseFileA()
    {
        var dlg = new OpenFileDialog { Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls" };
        if (dlg.ShowDialog() == true) FileAPath = dlg.FileName;
    }

    private void BrowseFileB()
    {
        var dlg = new OpenFileDialog { Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls" };
        if (dlg.ShowDialog() == true)
        {
            FileBPath = dlg.FileName;
        }
    }

    private void BrowseOutput()
    {
        string defaultName = !string.IsNullOrWhiteSpace(FileBPath)
            ? $"{Path.GetFileNameWithoutExtension(FileBPath)}_Reconciled.xlsx"
            : "Reconciled_Pricing_Schedule.xlsx";

        var dlg = new SaveFileDialog
        {
            Filter = "Excel Files (*.xlsx)|*.xlsx",
            FileName = defaultName
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

        try
        {
            IsLoading = true;
            StatusMessage = IsArabic ? "جارٍ فحص الملفات..." : "Verifying files schema & integrity...";
            VerificationReport = await _service.VerifyFilesAsync(FileAPath, FileBPath);
            StatusMessage = VerificationReport.IsValid
                ? (IsArabic ? "اكتمل الفحص بنجاح!" : "Pre-flight verification passed!")
                : (IsArabic ? "يوجد أخطاء في الفحص!" : "Verification failed!");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteReconcileAsync()
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

        try
        {
            IsLoading = true;
            ProgressPercentage = 10;
            StatusMessage = IsArabic ? "جارٍ قراءة الملفات ومطابقة البنود..." : "Reconciling line items with SIMD matcher...";

            var result = await _service.ReconcileAsync(FileAPath, FileBPath, Sensitivity);

            // Sort so that PRICED / MATCHED items appear at the very top!
            var sortedPairs = result.MatchedPairs
                .OrderByDescending(p => p.InjectedRate.HasValue && p.InjectedRate > 0 && !p.IsProvisionalSum)
                .ThenBy(p => p.IsProvisionalSum)
                .ThenBy(p => p.TargetItem.BillNumber)
                .ThenBy(p => p.TargetItem.AnchorRowIndex)
                .ToList();

            MatchedPairs.Clear();
            foreach (var p in sortedPairs)
            {
                MatchedPairs.Add(p);
            }
            FilteredItems.Refresh();

            Result = result;
            ProgressPercentage = 100;
            StatusMessage = IsArabic 
                ? $"اكتملت المطابقة في {result.ElapsedTime.TotalSeconds:F2} ثانية ({result.TotalTargetItems:N0} بند)" 
                : $"Reconciliation complete in {result.ElapsedTime.TotalSeconds:F2}s ({result.TotalTargetItems:N0} items)";

            SelectedTabIndex = 2; // Jump to Pricing Table tab
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show(
                (IsArabic ? "حدث خطأ أثناء المطابقة:\n" : "Error during reconciliation:\n") + ex.Message,
                IsArabic ? "خطأ في المطابقة" : "Matching Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteReconcileAndExportAsync()
    {
        await ExecuteReconcileAsync();
        if (Result != null && MatchedPairs.Count > 0)
        {
            await ExecuteExportAsync();
        }
    }

    private async Task ExecuteExportAsync()
    {
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

        try
        {
            IsLoading = true;
            ProgressPercentage = 10;
            StatusMessage = IsArabic ? "جارٍ إعداد وتجهيز ملفات الدمج والتصدير..." : "Preparing merge & export files...";

            UpdateDefaultOutputPath();
            if (string.IsNullOrWhiteSpace(OutputFilePath))
            {
                string dir = Path.GetDirectoryName(FileBPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string baseName = Path.GetFileNameWithoutExtension(FileBPath);
                OutputFilePath = Path.Combine(dir, $"{baseName}_Reconciled.xlsx");
            }

            string outDir = Path.GetDirectoryName(OutputFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string dashboardPath = Path.Combine(outDir, "DP3_Executive_Dashboard.xlsx");
            DashboardFilePath = dashboardPath;

            StatusMessage = IsArabic ? "جارٍ حقن الأسعار وإنشاء شيتات التدقيق والربط..." : "Injecting rates and building audit sheets...";
            var progress = new ThrottledProgress<int>(pct => ProgressPercentage = (int)(pct * 0.7), throttleIntervalMs: 50);

            // Automatically ensure all valid matched pairs are approved for injection
            foreach (var p in MatchedPairs)
            {
                if (p.InjectedRate.HasValue && !p.IsProvisionalSum && p.Confidence != MatchConfidence.ManualReviewNeeded)
                {
                    p.IsApproved = true;
                }
            }

            // 1. Export the Reconciled Schedule (with 33 original sheets + Audit_Report + Pricing_Linkage_Map with dynamic live links)
            await _service.ExportPricedScheduleAsync(
                FileBPath, 
                OutputFilePath, 
                MatchedPairs.ToList(), 
                FileAPath, 
                enableDynamicLinking: true, 
                progress);

            ProgressPercentage = 75;
            StatusMessage = IsArabic ? "جارٍ إنشاء لوحة المؤشرات المستقلة (Dashboard)..." : "Generating standalone Executive Dashboard...";

            // 2. Export the Standalone Executive Dashboard
            await Task.Run(() =>
            {
                ClosedXmlExporter.ExportStandaloneDashboard(dashboardPath, MatchedPairs.ToList(), FileAPath);
            });

            ProgressPercentage = 90;
            StatusMessage = IsArabic ? "جارٍ أرشفة السجل التاريخي..." : "Archiving snapshot to SQLite...";

            // 3. Persist snapshot to local SQLite repository
            await _service.SaveSnapshotAsync("DP3-REH", $"Rev-{DateTime.Now:yyyyMMdd-HHmm}", MatchedPairs.ToList());

            ProgressPercentage = 100;
            IsExported = true;
            LastExportedSchedulePath = OutputFilePath;
            LastExportedDashboardPath = dashboardPath;
            LastExportedFolder = outDir;

            StatusMessage = IsArabic 
                ? "تم دمج وتصدير ملف المقايسة والداش بورد وسجلات الربط بنجاح!" 
                : "Export completed and snapshot archived successfully!";

            // Prompt user with options to open files
            string msg = IsArabic
                ? $"تم الدمج والتصدير بنجاح!\n\n" +
                  $"1. ملف المقايسة المدمج:\n{OutputFilePath}\n" +
                  $"(يتضمن 33 ورقة عمل مسعرة + سجل الأحداث والتدقيق Audit_Report + خريطة ربط الأسعار Pricing_Linkage_Map مع التحديث التلقائي اللحظي)\n\n" +
                  $"2. لوحة مؤشرات الإدارة (Dashboard):\n{dashboardPath}\n\n" +
                  $"هل تريد فتح ملف المقايسة المسعر الآن في Excel؟"
                : $"Export completed successfully!\n\nReconciled Schedule:\n{OutputFilePath}\n\nExecutive Dashboard:\n{dashboardPath}\n\nDo you want to open the reconciled file now in Excel?";

            var res = MessageBox.Show(msg, IsArabic ? "اكتمل التصدير بنجاح" : "Export Success", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (res == MessageBoxResult.Yes)
            {
                OpenPricedFile();
            }
        }
        catch (IOException ioEx)
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
        catch (Exception ex)
        {
            StatusMessage = $"Export Error: {ex.Message}";
            MessageBox.Show(
                (IsArabic ? "حدث خطأ أثناء التصدير:\n" : "Error during export:\n") + ex.Message,
                IsArabic ? "خطأ في التصدير" : "Export Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
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

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
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
    }

    private bool FilterItemPredicate(object item)
    {
        if (item is not BoqMatchedPair pair) return false;

        // Text search filter
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            bool matchText = pair.TargetItem.Description.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                             pair.TargetItem.ItemCode.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                             pair.TargetItem.BillNumber.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase);
            if (!matchText) return false;
        }

        // Category filter
        return ActiveFilter switch
        {
            "Priced" => pair.InjectedRate.HasValue && pair.InjectedRate > 0 && !pair.IsProvisionalSum,
            "VO" => pair.IsVariationOrder,
            "PS" => pair.IsProvisionalSum,
            "Review" => pair.Confidence == MatchConfidence.ManualReviewNeeded,
            "Approved" => pair.IsApproved,
            _ => true
        };
    }

    #endregion

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        return SetProperty(ref field, value, propertyName);
    }
}
