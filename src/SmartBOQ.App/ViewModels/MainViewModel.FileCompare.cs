using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Parsers;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Partial class encapsulating multi-file ingestion, topological sheet linking,
/// column channel routing, and algorithmic execution plan orchestration.
/// Follows Clean Code separation of concerns.
/// </summary>
public sealed partial class MainViewModel
{
    public bool HasIngestedFiles => IngestedFiles.Count > 0;
    public bool HasNoIngestedFiles => IngestedFiles.Count == 0;

    #region Multi-File & Topology Initialization

    public void InitializeDefaultFiles()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string curDir = Directory.GetCurrentDirectory();

            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                curDir,
                Path.Combine(curDir, "file"),
                baseDir,
                Path.Combine(baseDir, "file")
            };

            // Traverse upward up to 6 parent levels to locate workspace 'file' directory
            var dirInfo = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dirInfo != null; i++)
            {
                string candidate = Path.Combine(dirInfo.FullName, "file");
                if (Directory.Exists(candidate)) candidateDirs.Add(candidate);
                if (File.Exists(Path.Combine(dirInfo.FullName, "DP3 - Hatchway.xlsx"))) candidateDirs.Add(dirInfo.FullName);
                dirInfo = dirInfo.Parent;
            }

            string foundA = string.Empty;
            string foundB = string.Empty;

            foreach (var dir in candidateDirs)
            {
                if (!Directory.Exists(dir)) continue;

                string pathA = Path.GetFullPath(Path.Combine(dir, "DP3 - Hatchway.xlsx"));
                if (string.IsNullOrEmpty(foundA) && File.Exists(pathA))
                {
                    foundA = pathA;
                }

                string pathB = Path.GetFullPath(Path.Combine(dir, "REH.08.26.3199 DP3 Pricing Schedule Re-Measure.xlsx"));
                if (string.IsNullOrEmpty(foundB) && File.Exists(pathB))
                {
                    foundB = pathB;
                }
            }

            var toIngest = new List<string>();
            if (!string.IsNullOrEmpty(foundA)) toIngest.Add(foundA);
            if (!string.IsNullOrEmpty(foundB)) toIngest.Add(foundB);

            if (toIngest.Count > 0)
            {
                _ = IngestFilesAsync(toIngest);
            }
            else
            {
                BuildAlgorithmicPlan();
            }
        }
        catch
        {
            BuildAlgorithmicPlan();
        }
    }

    #endregion

    #region File Ingestion & Management

    public async Task AddNewFileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls",
            Multiselect = true,
            Title = IsArabic ? "اختر ملفات المقايسة أو الأسعار" : "Select BOQ or Pricing Files"
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            await IngestFilesAsync(dlg.FileNames);
        }
    }

    /// <summary>
    /// Core ingestion method for single or multiple files from dialog or drag-and-drop.
    /// Preserves existing files and dynamically assigns roles without data loss.
    /// </summary>
    public async Task IngestFilesAsync(IEnumerable<string> filePaths)
    {
        IsLoading = true;
        StatusMessage = IsArabic ? "جارٍ تحليل واستكشاف بنية الملفات تلقائياً..." : "Inspecting imported workbooks...";

        try
        {
            var newInfos = new List<BoqFileInfo>();

            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                string fullPath = Path.GetFullPath(path);

                // If already ingested, remove old version to allow refreshing
                var existing = IngestedFiles.FirstOrDefault(f => f.FilePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    IngestedFiles.Remove(existing);
                }

                // Inspect file structure and contents (content-based role inference)
                var info = await _inspector.InspectWorkbookAsync(fullPath, preferredRole: null);
                newInfos.Add(info);
            }

            if (newInfos.Count == 0) return;

            foreach (var info in newInfos)
            {
                IngestedFiles.Add(info);
            }

            // Dynamically harmonize and reconcile file roles based on actual content & prices
            ReconcileFileRoles();

            SelectedFileForDetails = newInfos.LastOrDefault() ?? IngestedFiles.FirstOrDefault();
            await UpdateDiscoveryTopologyAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"خطأ في فحص الملف: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Reconciles and harmonizes file roles across all ingested workbooks based on pricing content and metadata.
    /// </summary>
    public void ReconcileFileRoles()
    {
        if (IngestedFiles.Count == 0) return;

        // Rule 1: Files with 0 priced items can NEVER be ContractorPriced or SupplementaryRates.
        // They are 100% ConsultantTarget schedules waiting for rates!
        foreach (var file in IngestedFiles.Where(f => !f.HasPricedRates))
        {
            file.Role = BoqFileRole.ConsultantTarget;
        }

        // Rule 2: Files containing positive rates
        var pricedFiles = IngestedFiles.Where(f => f.HasPricedRates).ToList();
        if (pricedFiles.Count > 0)
        {
            // Pick exactly one primary contractor pricing file:
            // Prefer file explicitly named CANDY/CCS/Contractor, or with highest priced items count
            var primaryContractor = pricedFiles.FirstOrDefault(f => f.FileName.Contains("candy", StringComparison.OrdinalIgnoreCase) ||
                                                                    f.FileName.Contains("ccs", StringComparison.OrdinalIgnoreCase))
                                   ?? pricedFiles.OrderByDescending(f => f.PricedItemsCount).FirstOrDefault();

            foreach (var pf in pricedFiles)
            {
                if (pf == primaryContractor)
                {
                    pf.Role = BoqFileRole.ContractorPriced;
                }
                else if (pf.DetectedRole == BoqFileRole.ConsultantTarget)
                {
                    pf.Role = BoqFileRole.ConsultantTarget;
                }
                else
                {
                    pf.Role = BoqFileRole.SupplementaryRates;
                }
            }
        }

        // Rule 3: Synchronize primary paths (prioritize package with highest bill items)
        var contractor = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced);
        var consultant = IngestedFiles
            .Where(f => f.Role == BoqFileRole.ConsultantTarget)
            .OrderByDescending(f => f.TotalEstimatedItems)
            .FirstOrDefault()
            ?? IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget);

        FileAPath = contractor?.FilePath ?? string.Empty;
        FileBPath = consultant?.FilePath ?? string.Empty;
    }

    /// <summary>
    /// Updates discovery state, sheet topology, and column routing without clearing IngestedFiles.
    /// </summary>
    public async Task UpdateDiscoveryTopologyAsync()
    {
        try
        {
            // Sync primary file paths
            var contractorFile = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced);
            var consultantFile = IngestedFiles
                .Where(f => f.Role == BoqFileRole.ConsultantTarget)
                .OrderByDescending(f => f.TotalEstimatedItems)
                .FirstOrDefault()
                ?? IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget);

            if (contractorFile != null) FileAPath = contractorFile.FilePath;
            if (consultantFile != null) FileBPath = consultantFile.FilePath;

            // Update details view
            if (SelectedFileForDetails == null || !IngestedFiles.Contains(SelectedFileForDetails))
            {
                SelectedFileForDetails = consultantFile ?? contractorFile ?? IngestedFiles.FirstOrDefault();
            }

            InspectedSheets.Clear();
            if (SelectedFileForDetails != null)
            {
                foreach (var s in SelectedFileForDetails.Sheets)
                {
                    InspectedSheets.Add(s);
                }
            }

            // Populate AvailableSourceSheetOptions for the linker
            AvailableSourceSheetOptions.Clear();
            AvailableSourceSheetOptions.Add("[مطابقة عامة في كامل المقايسة]");
            AvailableSourceSheetOptions.Add("[استثناء هذا الجدول / Skip]");

            var sourceFiles = IngestedFiles.Where(f => f.Role != BoqFileRole.ConsultantTarget).ToList();
            foreach (var src in sourceFiles)
            {
                foreach (var s in src.Sheets.Where(x => !x.IsNonBillSheet))
                {
                    string option = sourceFiles.Count > 1 
                        ? $"[{src.FileName}] {s.SheetName}" 
                        : s.SheetName;
                    AvailableSourceSheetOptions.Add(option);
                }
            }

            // Run topological sheet linker if target and source files exist
            if (consultantFile != null && sourceFiles.Count > 0)
            {
                var allSourceSheets = sourceFiles.SelectMany(f => f.Sheets).ToList();
                var autoLinks = await _inspector.AutoLinkSheetsAsync(consultantFile.Sheets, allSourceSheets);
                SheetLinkMappings.Clear();
                foreach (var link in autoLinks)
                {
                    SheetLinkMappings.Add(new SheetLinkMappingViewModel(link, AvailableSourceSheetOptions));
                }

                // Detect and route column channels
                if (contractorFile != null && File.Exists(contractorFile.FilePath) && File.Exists(consultantFile.FilePath))
                {
                    var colMapping = await _inspector.DetectColumnMappingAsync(contractorFile.FilePath, consultantFile.FilePath);
                    ActiveColumnMapping = colMapping;
                    PopulateColumnBindingOptions(contractorFile, consultantFile, colMapping);
                }
            }

            BuildAlgorithmicPlan();
            OnPropertyChanged(nameof(HasIngestedFiles));
            OnPropertyChanged(nameof(HasNoIngestedFiles));
            OnPropertyChanged(nameof(TotalDiscoveredTablesCount));
            OnPropertyChanged(nameof(TotalDiscoveredItemsCount));
            OnPropertyChanged(nameof(TotalLinkedTablesCount));
            OnPropertyChanged(nameof(TotalShieldedTablesCount));

            StatusMessage = IsArabic
                ? $"تم تسجيل {IngestedFiles.Count} ملفات وكشف {TotalDiscoveredTablesCount} جدولاً وربط {TotalLinkedTablesCount} جدول بنجاح."
                : $"Registered {IngestedFiles.Count} files, {TotalDiscoveredTablesCount} tables discovered, {TotalLinkedTablesCount} linked.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery notice: {ex.Message}";
        }
    }

    public void RemoveFile(BoqFileInfo? file)
    {
        if (file == null) return;
        IngestedFiles.Remove(file);

        if (file.FilePath.Equals(FileAPath, StringComparison.OrdinalIgnoreCase))
        {
            FileAPath = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced)?.FilePath ?? string.Empty;
        }
        if (file.FilePath.Equals(FileBPath, StringComparison.OrdinalIgnoreCase))
        {
            FileBPath = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget)?.FilePath ?? string.Empty;
        }

        _ = UpdateDiscoveryTopologyAsync();
    }

    public void SetPrimaryTarget(BoqFileInfo? file)
    {
        if (file == null) return;
        file.Role = BoqFileRole.ConsultantTarget;
        FileBPath = file.FilePath;
        _ = UpdateDiscoveryTopologyAsync();
    }

    public void SetPrimarySource(BoqFileInfo? file)
    {
        if (file == null) return;
        file.Role = BoqFileRole.ContractorPriced;
        FileAPath = file.FilePath;
        _ = UpdateDiscoveryTopologyAsync();
    }

    public void InspectFile(BoqFileInfo? file)
    {
        if (file == null) return;
        SelectedFileForDetails = file;
        InspectedSheets.Clear();
        foreach (var s in file.Sheets)
        {
            InspectedSheets.Add(s);
        }

        if (file.Role == BoqFileRole.ConsultantTarget)
        {
            FileBPath = file.FilePath;
            _ = AutoLinkSheetsAsync();
        }
        else if (file.Role == BoqFileRole.ContractorPriced || file.Role == BoqFileRole.SupplementaryRates)
        {
            FileAPath = file.FilePath;
        }

        SelectedCompareSubTab = 0; // Focus on tables inspector
    }

    public Task RefreshFilesAsync()
    {
        return UpdateDiscoveryTopologyAsync();
    }

    #endregion

    #region Sheet Linker & Column Channel Routing

    public async Task AutoLinkSheetsAsync()
    {
        var targetFile = IngestedFiles.FirstOrDefault(f => f.FilePath.Equals(FileBPath, StringComparison.OrdinalIgnoreCase))
                         ?? IngestedFiles.Where(f => f.Role == BoqFileRole.ConsultantTarget).OrderByDescending(f => f.TotalEstimatedItems).FirstOrDefault()
                         ?? IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget);
        var sourceFiles = IngestedFiles.Where(f => f.Role != BoqFileRole.ConsultantTarget && File.Exists(f.FilePath)).ToList();

        if (targetFile == null || sourceFiles.Count == 0) return;

        IsLoading = true;
        StatusMessage = IsArabic ? "جارٍ تشغيل خوارزمية الربط الذكي للجداول..." : "Running topological sheet auto-linker...";
        try
        {
            var sourceSheets = sourceFiles.SelectMany(f => f.Sheets).ToList();
            var links = await _inspector.AutoLinkSheetsAsync(targetFile.Sheets, sourceSheets);

            SheetLinkMappings.Clear();
            foreach (var link in links)
            {
                SheetLinkMappings.Add(new SheetLinkMappingViewModel(link, AvailableSourceSheetOptions));
            }

            OnPropertyChanged(nameof(TotalLinkedTablesCount));
            OnPropertyChanged(nameof(TotalShieldedTablesCount));
            BuildAlgorithmicPlan();

            StatusMessage = IsArabic ? "تم الربط الذكي للجداول بنجاح!" : "Sheet auto-linking completed!";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void LinkAllGlobal()
    {
        foreach (var m in SheetLinkMappings)
        {
            if (!m.IsProvisionalSum)
            {
                m.SelectedSourceSheet = "[مطابقة عامة في كامل المقايسة]";
            }
        }
        OnPropertyChanged(nameof(TotalLinkedTablesCount));
        BuildAlgorithmicPlan();
    }

    public async Task AutoDetectColumnsAsync()
    {
        var src = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ContractorPriced) ?? IngestedFiles.FirstOrDefault();
        var tgt = IngestedFiles.FirstOrDefault(f => f.Role == BoqFileRole.ConsultantTarget);

        if (src == null || tgt == null) return;

        IsLoading = true;
        StatusMessage = IsArabic ? "جارٍ الكشف التلقائي عن عناوين وقنوات الأعمدة..." : "Detecting column channels...";
        try
        {
            var detected = await _inspector.DetectColumnMappingAsync(src.FilePath, tgt.FilePath);
            ActiveColumnMapping = detected;
            PopulateColumnBindingOptions(src, tgt, detected);
            BuildAlgorithmicPlan();
            StatusMessage = detected.SummaryText;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void PopulateColumnBindingOptions(BoqFileInfo? src, BoqFileInfo? tgt, ColumnMappingModel mapping)
    {
        AvailableSourceColumns.Clear();
        AvailableTargetColumns.Clear();

        for (int i = 0; i < 26; i++)
        {
            string letter = BoqInspectorService.GetColumnLetter(i);
            string srcHeader = src?.AvailableColumns.ElementAtOrDefault(i) ?? string.Empty;
            string tgtHeader = tgt?.AvailableColumns.ElementAtOrDefault(i) ?? string.Empty;

            AvailableSourceColumns.Add(new ColumnBindingOption { ColumnIndex = i, ColumnLetter = letter, HeaderName = srcHeader });
            AvailableTargetColumns.Add(new ColumnBindingOption { ColumnIndex = i, ColumnLetter = letter, HeaderName = tgtHeader });
        }

        SelectedSourceRateCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == mapping.SourceRateColumn) ?? AvailableSourceColumns.ElementAtOrDefault(17);
        SelectedTargetRateCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == mapping.TargetRateColumn) ?? AvailableTargetColumns.ElementAtOrDefault(6);

        SelectedSourceDescCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == mapping.SourceDescColumn) ?? AvailableSourceColumns.ElementAtOrDefault(11);
        SelectedTargetDescCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == mapping.TargetDescColumn) ?? AvailableTargetColumns.ElementAtOrDefault(2);

        SelectedSourceCodeCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == mapping.SourceCodeColumn) ?? AvailableSourceColumns.ElementAtOrDefault(10);
        SelectedTargetCodeCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == mapping.TargetCodeColumn) ?? AvailableTargetColumns.ElementAtOrDefault(0);

        SelectedSourceQtyCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == mapping.SourceQtyColumn) ?? AvailableSourceColumns.ElementAtOrDefault(14);
        SelectedTargetQtyCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == mapping.TargetQtyColumn) ?? AvailableTargetColumns.ElementAtOrDefault(4);

        SelectedSourceUnitCol = AvailableSourceColumns.FirstOrDefault(c => c.ColumnIndex == mapping.SourceUnitColumn) ?? AvailableSourceColumns.ElementAtOrDefault(13);
        SelectedTargetUnitCol = AvailableTargetColumns.FirstOrDefault(c => c.ColumnIndex == mapping.TargetUnitColumn) ?? AvailableTargetColumns.ElementAtOrDefault(5);

        OnPropertyChanged(nameof(SampleRoutedDataText));
    }

    #endregion

    #region Algorithmic Plan Orchestration

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
            Details = $"فحص {IngestedFiles.Count} ملفات واستكشاف {TotalDiscoveredTablesCount} جدولاً تشمل {TotalShieldedTablesCount} جدول مبالغ احتياطية و {TotalDiscoveredItemsCount:N0} بند تقديري."
        });

        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 2,
            Title = "محاذاة وربط الشيتات (Sheet Linker)",
            Subtitle = "مطابقة الجداول والأبواب وحماية المبالغ الاحتياطية",
            Icon = "GitMerge",
            StatusText = $"{TotalLinkedTablesCount} جدول مرتبط",
            IsActive = true,
            Details = $"محاذاة الجداول بناءً على رمز الباب واسم الشيت مع دعم البحث الشامل وحماية الـ PS بنسبة 100%."
        });

        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 3,
            Title = "توجيه قنوات الأعمدة (Column Channel Routing)",
            Subtitle = "تحديد مسارات نقل الأسعار والأوصاف والأكواد",
            Icon = "ArrowRightLeft",
            StatusText = $"قناة السعر: {SelectedSourceRateCol?.ColumnLetter ?? "R"} ➔ {SelectedTargetRateCol?.ColumnLetter ?? "G"}",
            IsActive = true,
            Details = $"سحب سعر الوحدة من العمود ({SelectedSourceRateCol?.ColumnLetter ?? "R"}) وحقنه في ({SelectedTargetRateCol?.ColumnLetter ?? "G"}) مع مطابقة كود البند ({SelectedSourceCodeCol?.ColumnLetter ?? "K"} ➔ {SelectedTargetCodeCol?.ColumnLetter ?? "A"})."
        });

        AlgorithmicSteps.Add(new AlgorithmicPlanStep
        {
            StepNumber = 4,
            Title = "محرك مطابقة الأسعار (SIMD Matching Engine)",
            Subtitle = "معالجة متوازية فائقة السرعة وفهرسة فورية",
            Icon = "Cpu",
            StatusText = $"حساسية: {Sensitivity:P0}",
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

    #endregion
}
