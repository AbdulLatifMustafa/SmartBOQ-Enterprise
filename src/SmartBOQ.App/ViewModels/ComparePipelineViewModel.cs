using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmartBOQ.App.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Specialized sub-viewmodel managing the 4-phase file compare and ingestion pipeline:
/// 1. Files Discovery & Inspection
/// 2. Cross-Workbook Sheet Linker
/// 3. Column Channels & Semantic Routing
/// 4. Algorithmic Execution Flow
/// Inherits from <see cref="ChildViewModelBase"/>.
/// </summary>
public sealed class ComparePipelineViewModel : ChildViewModelBase
{
    private int _selectedCompareSubTab = 0;
    private BoqFileInfo? _selectedFileForDetails;
    private ColumnMappingModel _activeColumnMapping = new();
    private string _scopeStrategy = "TableIsolated";
    private bool _priorityCodeMatching = true;
    private bool _shieldProvisionalSums = true;
    private bool _strictCurrencyIsolation = true;

    // Column Routing Channels
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

    // Collections
    public ObservableCollection<BoqFileInfo> IngestedFiles { get; } = [];
    public ObservableCollection<BoqSheetSummary> InspectedSheets { get; } = [];
    public ObservableCollection<SheetLinkMappingViewModel> SheetLinkMappings { get; } = [];
    public ObservableCollection<string> AvailableSourceSheetOptions { get; } = [];
    public ObservableCollection<ColumnBindingOption> AvailableSourceColumns { get; } = [];
    public ObservableCollection<ColumnBindingOption> AvailableTargetColumns { get; } = [];
    public ObservableCollection<AlgorithmicPlanStep> AlgorithmicSteps { get; } = [];
    public ObservableCollection<MappingPreset> SavedPresets { get; } = [];

    // Properties
    public int SelectedCompareSubTab { get => _selectedCompareSubTab; set => SetProperty(ref _selectedCompareSubTab, value); }
    public BoqFileInfo? SelectedFileForDetails
    {
        get => _selectedFileForDetails;
        set
        {
            if (SetProperty(ref _selectedFileForDetails, value))
            {
                PopulateSheetsForFile(value);
            }
        }
    }

    public ColumnMappingModel ActiveColumnMapping { get => _activeColumnMapping; set => SetProperty(ref _activeColumnMapping, value); }
    public string ScopeStrategy { get => _scopeStrategy; set => SetProperty(ref _scopeStrategy, value); }
    public bool PriorityCodeMatching { get => _priorityCodeMatching; set => SetProperty(ref _priorityCodeMatching, value); }
    public bool ShieldProvisionalSums { get => _shieldProvisionalSums; set => SetProperty(ref _shieldProvisionalSums, value); }
    public bool StrictCurrencyIsolation { get => _strictCurrencyIsolation; set => SetProperty(ref _strictCurrencyIsolation, value); }

    public ColumnBindingOption? SelectedSourceRateCol { get => _selectedSourceRateCol; set => SetProperty(ref _selectedSourceRateCol, value); }
    public ColumnBindingOption? SelectedTargetRateCol { get => _selectedTargetRateCol; set => SetProperty(ref _selectedTargetRateCol, value); }
    public ColumnBindingOption? SelectedSourceDescCol { get => _selectedSourceDescCol; set => SetProperty(ref _selectedSourceDescCol, value); }
    public ColumnBindingOption? SelectedTargetDescCol { get => _selectedTargetDescCol; set => SetProperty(ref _selectedTargetDescCol, value); }
    public ColumnBindingOption? SelectedSourceCodeCol { get => _selectedSourceCodeCol; set => SetProperty(ref _selectedSourceCodeCol, value); }
    public ColumnBindingOption? SelectedTargetCodeCol { get => _selectedTargetCodeCol; set => SetProperty(ref _selectedTargetCodeCol, value); }
    public ColumnBindingOption? SelectedSourceQtyCol { get => _selectedSourceQtyCol; set => SetProperty(ref _selectedSourceQtyCol, value); }
    public ColumnBindingOption? SelectedTargetQtyCol { get => _selectedTargetQtyCol; set => SetProperty(ref _selectedTargetQtyCol, value); }
    public ColumnBindingOption? SelectedSourceUnitCol { get => _selectedSourceUnitCol; set => SetProperty(ref _selectedSourceUnitCol, value); }
    public ColumnBindingOption? SelectedTargetUnitCol { get => _selectedTargetUnitCol; set => SetProperty(ref _selectedTargetUnitCol, value); }

    // Metrics
    public int TotalDiscoveredTablesCount => IngestedFiles.Sum(f => f.Sheets.Count);
    public int TotalLinkedTablesCount => SheetLinkMappings.Count(m => m.Status != SheetLinkStatus.Excluded && !string.IsNullOrWhiteSpace(m.SelectedSourceSheet));
    public int TotalShieldedTablesCount => SheetLinkMappings.Count(m => m.IsProvisionalSum || m.Status == SheetLinkStatus.ShieldedPS);
    public bool HasIngestedFiles => IngestedFiles.Count > 0;
    public bool HasNoIngestedFiles => IngestedFiles.Count == 0;

    private MappingPreset? _selectedPreset;
    private string? _newPresetName;

    public MappingPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value) && value != null)
            {
                ApplyPreset(value);
            }
        }
    }

    public string? NewPresetName { get => _newPresetName; set => SetProperty(ref _newPresetName, value); }

    // Commands
    public AsyncRelayCommand AddNewFileCommand { get; }
    public RelayCommand RemoveFileCommand { get; }
    public RelayCommand SetPrimaryTargetCommand { get; }
    public RelayCommand SetPrimaryContractorCommand { get; }
    public RelayCommand SetPrimaryConsultantCommand { get; }
    public AsyncRelayCommand AutoLinkSheetsCommand { get; }
    public RelayCommand AddSheetMappingCommand { get; }
    public RelayCommand RemoveSheetMappingCommand { get; }
    public RelayCommand ClearSheetMappingsCommand { get; }
    public RelayCommand ApplyColumnMappingCommand { get; }
    public RelayCommand ResetColumnMappingCommand { get; }
    public AsyncRelayCommand RunPreFlightCheckCommand { get; }
    public RelayCommand OpenSheetLocationCommand { get; }
    public RelayCommand OpenLinkedSheetLocationCommand { get; }
    public RelayCommand OpenLinkedSourceSheetLocationCommand { get; }
    public AsyncRelayCommand SaveCurrentPresetCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public AsyncRelayCommand DeletePresetCommand { get; }
    public AsyncRelayCommand RefreshPresetsCommand { get; }

    public ComparePipelineViewModel(IMainViewModelCoordinator coordinator) : base(coordinator)
    {
        AddNewFileCommand = new AsyncRelayCommand(AddNewFileAsync);
        RemoveFileCommand = new RelayCommand(p => RemoveFile(p as BoqFileInfo));
        SetPrimaryTargetCommand = new RelayCommand(p => SetPrimaryTarget(p as BoqFileInfo));
        SetPrimaryContractorCommand = new RelayCommand(p => SetPrimarySource(p as BoqFileInfo));
        SetPrimaryConsultantCommand = new RelayCommand(p => SetPrimaryTarget(p as BoqFileInfo));
        AutoLinkSheetsCommand = new AsyncRelayCommand(AutoLinkSheetsAsync);
        AddSheetMappingCommand = new RelayCommand(_ => AddSheetMapping());
        RemoveSheetMappingCommand = new RelayCommand(p => RemoveSheetMapping(p as SheetLinkMappingViewModel));
        ClearSheetMappingsCommand = new RelayCommand(_ => ClearSheetMappings());
        ApplyColumnMappingCommand = new RelayCommand(_ => ApplyColumnMapping());
        ResetColumnMappingCommand = new RelayCommand(_ => ResetColumnMapping());
        RunPreFlightCheckCommand = new AsyncRelayCommand(RunPreFlightCheckAsync);
        OpenSheetLocationCommand = new RelayCommand(p => OpenSheetLocation(p as BoqSheetSummary));
        OpenLinkedSheetLocationCommand = new RelayCommand(p => OpenLinkedSheetLocation(p as SheetLinkMappingViewModel));
        OpenLinkedSourceSheetLocationCommand = new RelayCommand(p => OpenLinkedSourceSheetLocation(p as SheetLinkMappingViewModel));
        SaveCurrentPresetCommand = new AsyncRelayCommand(() => SaveCurrentPresetAsync());
        ApplyPresetCommand = new RelayCommand(p => ApplyPreset(p as MappingPreset ?? SelectedPreset));
        DeletePresetCommand = new AsyncRelayCommand(() => DeletePresetAsync(SelectedPreset));
        RefreshPresetsCommand = new AsyncRelayCommand(RefreshPresetsAsync);

        BuildAlgorithmicPlan();
        _ = RefreshPresetsAsync();
    }

    public async Task AddNewFileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls|All Files (*.*)|*.*",
            Multiselect = true,
            Title = "إضافة ملف مقايسة جديد إلى المنظومة"
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                await IngestAndInspectFileAsync(file);
            }
        }
    }

    public async Task IngestAndInspectFileAsync(string filePath, BoqFileRole? preferredRole = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        // Prevent duplicate ingestion of same physical file
        if (IngestedFiles.Any(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            SetStatus($"جارٍ الفحص التكيفي للملف: {Path.GetFileName(filePath)}...");
            var info = await Inspector.InspectWorkbookAsync(filePath, preferredRole);

            // Dynamically assign role based on content pricing & metadata when no explicit preference is set
            if (preferredRole == null)
            {
                if (!info.HasPricedRates)
                {
                    info.Role = BoqFileRole.ConsultantTarget;
                }
                else
                {
                    bool hasContractor = IngestedFiles.Any(f => f.Role == BoqFileRole.ContractorPriced);
                    if (info.DetectedRole == BoqFileRole.ConsultantTarget)
                    {
                        info.Role = BoqFileRole.ConsultantTarget;
                    }
                    else if (!hasContractor)
                    {
                        info.Role = BoqFileRole.ContractorPriced;
                    }
                    else
                    {
                        info.Role = BoqFileRole.SupplementaryRates;
                    }
                }
            }

            IngestedFiles.Add(info);
            SelectedFileForDetails = info;

            OnPropertyChanged(nameof(TotalDiscoveredTablesCount));
            OnPropertyChanged(nameof(HasIngestedFiles));
            OnPropertyChanged(nameof(HasNoIngestedFiles));

            await AutoLinkSheetsAsync();
            BuildAlgorithmicPlan();
            SetStatus($"تم فحص الملف بنجاح ({info.Sheets.Count} جدول).");
        }, "جارٍ فحص الملف...");
    }

    public void RemoveFile(BoqFileInfo? file)
    {
        if (file == null) return;
        IngestedFiles.Remove(file);
        if (SelectedFileForDetails == file)
        {
            SelectedFileForDetails = IngestedFiles.FirstOrDefault();
        }

        OnPropertyChanged(nameof(TotalDiscoveredTablesCount));
        OnPropertyChanged(nameof(HasIngestedFiles));
        OnPropertyChanged(nameof(HasNoIngestedFiles));

        _ = AutoLinkSheetsAsync();
        BuildAlgorithmicPlan();
    }

    public void SetPrimaryTarget(BoqFileInfo? file)
    {
        if (file == null) return;
        file.Role = BoqFileRole.ConsultantTarget;

        _ = AutoLinkSheetsAsync();
        BuildAlgorithmicPlan();
    }

    public void SetPrimarySource(BoqFileInfo? file)
    {
        if (file == null) return;
        foreach (var f in IngestedFiles.Where(x => x.Role == BoqFileRole.ContractorPriced && x != file))
        {
            f.Role = BoqFileRole.SupplementaryRates;
        }
        file.Role = BoqFileRole.ContractorPriced;

        _ = AutoLinkSheetsAsync();
        BuildAlgorithmicPlan();
    }

    private void PopulateSheetsForFile(BoqFileInfo? file)
    {
        InspectedSheets.Clear();
        if (file == null) return;

        foreach (var s in file.Sheets)
        {
            InspectedSheets.Add(s);
        }
    }

    public async Task AutoLinkSheetsAsync()
    {
        var targetFile = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget) ?? IngestedFiles.LastOrDefault();
        var sourceFile = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced) ?? IngestedFiles.FirstOrDefault();

        if (targetFile == null || sourceFile == null) return;

        var targetSummaries = targetFile.Sheets;
        var sourceSummaries = sourceFile.Sheets;

        var mappings = await Inspector.AutoLinkSheetsAsync(targetSummaries, sourceSummaries);

        AvailableSourceSheetOptions.Clear();
        AvailableSourceSheetOptions.Add("[بحث عام في جميع الجداول]");
        AvailableSourceSheetOptions.Add("[تخطي هذا الجدول]");
        foreach (var s in sourceSummaries)
        {
            AvailableSourceSheetOptions.Add(s.SheetName);
        }

        SheetLinkMappings.Clear();
        foreach (var m in mappings)
        {
            var vm = new SheetLinkMappingViewModel(m, AvailableSourceSheetOptions);
            SheetLinkMappings.Add(vm);
        }

        OnPropertyChanged(nameof(TotalLinkedTablesCount));
        OnPropertyChanged(nameof(TotalShieldedTablesCount));
    }

    public void AddSheetMapping()
    {
        var mapping = new SheetLinkMapping
        {
            TargetSheetName = "جدول جديد",
            TargetBillCode = "CUSTOM",
            SelectedSourceSheet = AvailableSourceSheetOptions.FirstOrDefault() ?? string.Empty
        };
        var vm = new SheetLinkMappingViewModel(mapping, AvailableSourceSheetOptions);
        SheetLinkMappings.Add(vm);
        OnPropertyChanged(nameof(TotalLinkedTablesCount));
    }

    public void RemoveSheetMapping(SheetLinkMappingViewModel? vm)
    {
        if (vm == null) return;
        SheetLinkMappings.Remove(vm);
        OnPropertyChanged(nameof(TotalLinkedTablesCount));
    }

    public void ClearSheetMappings()
    {
        SheetLinkMappings.Clear();
        OnPropertyChanged(nameof(TotalLinkedTablesCount));
        OnPropertyChanged(nameof(TotalShieldedTablesCount));
    }

    public void ApplyColumnMapping()
    {
        ActiveColumnMapping = new ColumnMappingModel
        {
            SourceRateColumn = SelectedSourceRateCol?.ColumnIndex ?? -1,
            TargetRateColumn = SelectedTargetRateCol?.ColumnIndex ?? -1,
            SourceDescColumn = SelectedSourceDescCol?.ColumnIndex ?? -1,
            TargetDescColumn = SelectedTargetDescCol?.ColumnIndex ?? -1,
            SourceCodeColumn = SelectedSourceCodeCol?.ColumnIndex ?? -1,
            TargetCodeColumn = SelectedTargetCodeCol?.ColumnIndex ?? -1,
            SourceQtyColumn = SelectedSourceQtyCol?.ColumnIndex ?? -1,
            TargetQtyColumn = SelectedTargetQtyCol?.ColumnIndex ?? -1,
            SourceUnitColumn = SelectedSourceUnitCol?.ColumnIndex ?? -1,
            TargetUnitColumn = SelectedTargetUnitCol?.ColumnIndex ?? -1
        };

        SetStatus("تم تطبيق قنوات الأعمدة المخصصة بنجاح.");
    }

    public void ResetColumnMapping()
    {
        SelectedSourceRateCol = null;
        SelectedTargetRateCol = null;
        SelectedSourceDescCol = null;
        SelectedTargetDescCol = null;
        SelectedSourceCodeCol = null;
        SelectedTargetCodeCol = null;
        SelectedSourceQtyCol = null;
        SelectedTargetQtyCol = null;
        SelectedSourceUnitCol = null;
        SelectedTargetUnitCol = null;

        ActiveColumnMapping = new ColumnMappingModel();
        SetStatus("تم إعادة تعيين قنوات الأعمدة للوضع التلقائي.");
    }

    public async Task RunPreFlightCheckAsync()
    {
        var targetFile = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget);
        if (targetFile == null || !File.Exists(targetFile.FilePath))
        {
            MessageBox.Show("يرجى تحديد ملف الاستشاري الأساسي أولاً للتحقق.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ExecuteAsync(async () =>
        {
            var contractorFile = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced);
            string fileA = contractorFile?.FilePath ?? targetFile.FilePath;
            var report = await Service.VerifyFilesAsync(fileA, targetFile.FilePath);
            string msg = report.IsValid
                ? $"الفحص الاستباقي سليم بنجاح!\n\nعدد الجداول: {report.FileBSheetsCount}\nإجمالي الصفوف المقروءة: {report.FileARowsCount}"
                : $"تم رصد ملاحظات أثناء الفحص:\n{string.Join("\n", report.Errors)}";

            MessageBox.Show(msg, "تقرير الفحص الاستباقي", MessageBoxButton.OK, report.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }, "جارٍ إجراء الفحص الاستباقي...");
    }

    public void BuildAlgorithmicPlan()
    {
        AlgorithmicSteps.Clear();
        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 1,
            Title = "استكشاف وهيكل الملفات (Data Ingestion)",
            Subtitle = "كشف الجداول والصفوف التقديرية بدقة وسرعة",
            Icon = "FileSpreadsheet",
            StatusText = $"{TotalDiscoveredTablesCount} جدول مكتشف",
            IsActive = true,
            Details = $"فحص {IngestedFiles.Count} ملفات واستكشاف {TotalDiscoveredTablesCount} جدولاً تشمل {TotalShieldedTablesCount} جدول مبالغ احتياطية."
        });
        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 2,
            Title = "محاذاة وربط الشيتات (Sheet Linker)",
            Subtitle = "مطابقة الجداول والأبواب وحماية المبالغ الاحتياطية",
            Icon = "GitMerge",
            StatusText = $"{TotalLinkedTablesCount} جدول مرتبط",
            IsActive = true,
            Details = "محاذاة الجداول بناءً على رمز الباب واسم الشيت مع دعم البحث الشامل وحماية الـ PS بنسبة 100%."
        });
        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 3,
            Title = "توجيه قنوات الأعمدة (Column Channel Routing)",
            Subtitle = "تحديد مسارات نقل الأسعار والأوصاف والأكواد",
            Icon = "ArrowRightLeft",
            StatusText = $"قناة السعر: {SelectedSourceRateCol?.ColumnLetter ?? "R"} ➔ {SelectedTargetRateCol?.ColumnLetter ?? "G"}",
            IsActive = true,
            Details = $"سحب سعر الوحدة من العمود ({SelectedSourceRateCol?.ColumnLetter ?? "R"}) وحقنه في ({SelectedTargetRateCol?.ColumnLetter ?? "G"})."
        });
        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 4,
            Title = "محرك مطابقة الأسعار (SIMD Matching Engine)",
            Subtitle = "معالجة متوازية فائقة السرعة وفهرسة فورية",
            Icon = "Cpu",
            StatusText = "فهرسة فورية نشطة",
            IsActive = true,
            Details = "مطابقة فورية في أجزاء من الثانية مع أولوية مطلقة لكود البند ومطابقة نصية دقيقة."
        });
        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 5,
            Title = "مخرجات Excel الحية (Live Excel Delivery)",
            Subtitle = "حقن الصيغ الديناميكية وتوليد تقرير التدقيق",
            Icon = "FileSpreadsheet",
            StatusText = "معادلات حية نشطة",
            IsActive = true,
            Details = "حقن معادلات الربط الديناميكي النسبي OpenXML مع توليد كشف الفروق وشيت التدقيق المالي."
        });
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
        var targetFile = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget);
        if (targetFile != null && File.Exists(targetFile.FilePath))
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
            sheetName = null;
        }

        var sourceFile = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced);
        if (sourceFile != null && File.Exists(sourceFile.FilePath))
        {
            ExcelNavigator.OpenWorkbookAtSheet(sourceFile.FilePath, sheetName, mapping.SourceStartRow);
        }
    }

    #region Mapping Presets Engine

    public async Task RefreshPresetsAsync()
    {
        try
        {
            var presets = await Service.GetMappingPresetsAsync();
            SavedPresets.Clear();
            foreach (var p in presets)
            {
                SavedPresets.Add(p);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load presets: {ex.Message}");
        }
    }

    public async Task SaveCurrentPresetAsync(object? parameter = null)
    {
        string? name = parameter as string ?? NewPresetName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Preset_{DateTime.Now:yyyyMMdd_HHmm}";
        }

        var preset = new MappingPreset
        {
            PresetName = name,
            ContractorName = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced)?.FileName ?? string.Empty,
            SourceRateCol = SelectedSourceRateCol?.ColumnIndex ?? -1,
            TargetRateCol = SelectedTargetRateCol?.ColumnIndex ?? -1,
            SourceDescCol = SelectedSourceDescCol?.ColumnIndex ?? -1,
            TargetDescCol = SelectedTargetDescCol?.ColumnIndex ?? -1,
            SourceCodeCol = SelectedSourceCodeCol?.ColumnIndex ?? -1,
            TargetCodeCol = SelectedTargetCodeCol?.ColumnIndex ?? -1,
            SourceQtyCol = SelectedSourceQtyCol?.ColumnIndex ?? -1,
            TargetQtyCol = SelectedTargetQtyCol?.ColumnIndex ?? -1,
            SourceUnitCol = SelectedSourceUnitCol?.ColumnIndex ?? -1,
            TargetUnitCol = SelectedTargetUnitCol?.ColumnIndex ?? -1,
            CreatedAt = DateTime.UtcNow
        };

        await ExecuteAsync(async () =>
        {
            await Service.SaveMappingPresetAsync(preset);
            await RefreshPresetsAsync();
            SelectedPreset = SavedPresets.FirstOrDefault(p => p.PresetName == name);
            NewPresetName = string.Empty;
            SetStatus($"تم حفظ إعداد التوجيه والقنوات بنجاح: {name}");
        }, "جارٍ حفظ إعداد القنوات...");
    }

    public void ApplyPreset(MappingPreset? preset)
    {
        if (preset == null) return;

        if (preset.SourceRateCol >= 0)
            SelectedSourceRateCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == preset.SourceRateCol) ?? SelectedSourceRateCol;
        if (preset.TargetRateCol >= 0)
            SelectedTargetRateCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == preset.TargetRateCol) ?? SelectedTargetRateCol;

        if (preset.SourceDescCol >= 0)
            SelectedSourceDescCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == preset.SourceDescCol) ?? SelectedSourceDescCol;
        if (preset.TargetDescCol >= 0)
            SelectedTargetDescCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == preset.TargetDescCol) ?? SelectedTargetDescCol;

        if (preset.SourceCodeCol >= 0)
            SelectedSourceCodeCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == preset.SourceCodeCol) ?? SelectedSourceCodeCol;
        if (preset.TargetCodeCol >= 0)
            SelectedTargetCodeCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == preset.TargetCodeCol) ?? SelectedTargetCodeCol;

        if (preset.SourceQtyCol >= 0)
            SelectedSourceQtyCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == preset.SourceQtyCol) ?? SelectedSourceQtyCol;
        if (preset.TargetQtyCol >= 0)
            SelectedTargetQtyCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == preset.TargetQtyCol) ?? SelectedTargetQtyCol;

        if (preset.SourceUnitCol >= 0)
            SelectedSourceUnitCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == preset.SourceUnitCol) ?? SelectedSourceUnitCol;
        if (preset.TargetUnitCol >= 0)
            SelectedTargetUnitCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == preset.TargetUnitCol) ?? SelectedTargetUnitCol;

        ApplyColumnMapping();
        SetStatus($"تم تطبيق إعداد التوجيه: {preset.PresetName}");
    }

    public async Task DeletePresetAsync(MappingPreset? preset)
    {
        if (preset == null) return;
        await ExecuteAsync(async () =>
        {
            await Service.DeleteMappingPresetAsync(preset.PresetName);
            await RefreshPresetsAsync();
            if (SelectedPreset?.PresetName == preset.PresetName)
            {
                SelectedPreset = null;
            }
            SetStatus($"تم حذف إعداد التوجيه: {preset.PresetName}");
        }, "جارٍ حذف إعداد القنوات...");
    }

    #endregion
}
