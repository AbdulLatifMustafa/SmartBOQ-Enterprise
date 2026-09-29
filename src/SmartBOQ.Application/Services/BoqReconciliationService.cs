using System.Diagnostics;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Application.Services;

/// <summary>
/// Result container for the end-to-end reconciliation pipeline.
/// </summary>
public sealed record ReconciliationResult
{
    public required IReadOnlyList<BoqMatchedPair> MatchedPairs { get; init; }
    public required IReadOnlyList<BoqSheet> TargetSheets { get; init; }
    public required IReadOnlyList<BoqItem> SourceItems { get; init; }
    public required IReadOnlyList<CurrencyBucketSummary> CurrencySummaries { get; init; }

    public int TotalTargetItems => MatchedPairs.Count;
    public int ExactMatches => MatchedPairs.Count(p => p.Confidence == MatchConfidence.Exact);
    public int HighFuzzyMatches => MatchedPairs.Count(p => p.Confidence == MatchConfidence.HighFuzzy);
    public int ReviewNeeded => MatchedPairs.Count(p => p.Confidence == MatchConfidence.ManualReviewNeeded);
    public int VariationOrders => MatchedPairs.Count(p => p.IsVariationOrder);
    public int ProvisionalSumsShielded => MatchedPairs.Count(p => p.TargetItem.Type == BoqItemType.ProvisionalSum);
    public TimeSpan ElapsedTime { get; init; }
}

/// <summary>
/// Result of a single reconciled and exported target in a batch multi-target reconciliation run.
/// </summary>
public sealed record BatchTargetResult
{
    public required string TargetFilePath { get; init; }
    public required string OutputFilePath { get; init; }
    public string TargetFileName => Path.GetFileName(TargetFilePath);
    public string OutputFileName => Path.GetFileName(OutputFilePath);
    public required ReconciliationResult Result { get; init; }
    public int TotalItems => Result.TotalTargetItems;
    public int MatchedItems => Result.ExactMatches + Result.HighFuzzyMatches;
    public double MatchRate => TotalItems > 0 ? (double)MatchedItems / TotalItems * 100.0 : 0.0;
}

/// <summary>
/// Application orchestration service coordinating verification, reading, matching,
/// segregated financial calculations, and template export.
/// </summary>
public sealed class BoqReconciliationService
{
    private readonly IVerificationGate _verificationGate;
    private readonly IBoqReader _flatReader;
    private readonly IBoqReader _hierarchicalReader;
    private readonly IItemMatcher _matcher;
    private readonly IBoqExporter _exporter;
    private readonly ISqliteRepository _repository;
    private readonly IBoqInspector? _inspector;

    public BoqReconciliationService(
        IVerificationGate verificationGate,
        IBoqReader flatReader,
        IBoqReader hierarchicalReader,
        IItemMatcher matcher,
        IBoqExporter exporter,
        ISqliteRepository repository,
        IBoqInspector? inspector = null)
    {
        _verificationGate = verificationGate;
        _flatReader = flatReader;
        _hierarchicalReader = hierarchicalReader;
        _matcher = matcher;
        _exporter = exporter;
        _repository = repository;
        _inspector = inspector;
    }

    /// <summary>
    /// Inspects an Excel workbook structure, detecting sheets, estimated rows, and columns without full model loading.
    /// </summary>
    public Task<BoqFileInfo> InspectWorkbookAsync(string filePath, BoqFileRole? role = null, CancellationToken ct = default)
    {
        if (_inspector != null)
        {
            return _inspector.InspectWorkbookAsync(filePath, role, ct);
        }
        throw new InvalidOperationException("BoqInspector is not registered.");
    }

    /// <summary>
    /// Computes automatic topological sheet and table link recommendations.
    /// </summary>
    public Task<IReadOnlyList<SheetLinkMapping>> AutoLinkSheetsAsync(
        IReadOnlyList<BoqSheetSummary> targetSheets,
        IReadOnlyList<BoqSheetSummary> sourceSheets,
        CancellationToken ct = default)
    {
        if (_inspector != null)
        {
            return _inspector.AutoLinkSheetsAsync(targetSheets, sourceSheets, ct);
        }
        return Task.FromResult<IReadOnlyList<SheetLinkMapping>>(Array.Empty<SheetLinkMapping>());
    }

    /// <summary>
    /// Detects candidate columns for prices, quantities, and descriptions.
    /// </summary>
    public Task<ColumnMappingModel> DetectColumnMappingAsync(string sourceFilePath, string targetFilePath, CancellationToken ct = default)
    {
        if (_inspector != null)
        {
            return _inspector.DetectColumnMappingAsync(sourceFilePath, targetFilePath, ct);
        }
        return Task.FromResult(new ColumnMappingModel());
    }

    /// <summary>
    /// Executes pre-flight schema and integrity check.
    /// </summary>
    public Task<VerificationReport> VerifyFilesAsync(string fileAPath, string fileBPath, CancellationToken ct = default)
    {
        return _verificationGate.VerifyFilesAsync(fileAPath, fileBPath, ct);
    }


    /// <summary>
    /// Executes full end-to-end reconciliation between contractor reference and consultant schedule.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync(
        string fileAPath,
        string fileBPath,
        double sensitivity = 0.85,
        CancellationToken ct = default)
    {
        if (_inspector != null)
        {
            var infoA = await _inspector.InspectWorkbookAsync(fileAPath, BoqFileRole.ContractorPriced, ct).ConfigureAwait(false);
            var infoB = await _inspector.InspectWorkbookAsync(fileBPath, BoqFileRole.ConsultantTarget, ct).ConfigureAwait(false);
            var links = await _inspector.AutoLinkSheetsAsync(infoB.Sheets, infoA.Sheets, ct).ConfigureAwait(false);
            var colMap = await _inspector.DetectColumnMappingAsync(fileAPath, fileBPath, ct).ConfigureAwait(false);

            return await ReconcileMultiSourceAsync(
                new[] { fileAPath },
                fileBPath,
                sensitivity,
                links,
                colMap,
                ct).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();

        // 1. Parallel dual-stream parsing of File A and File B
        var taskA = _flatReader.ReadContractorFlatBoqAsync(fileAPath, ct);
        var taskB = _hierarchicalReader.ReadConsultantHierarchicalBoqAsync(fileBPath, ct);
        await Task.WhenAll(taskA, taskB).ConfigureAwait(false);

        var sourceItems = await taskA;
        var targetSheets = await taskB;

        // 3. Flatten target items for matching
        var allTargetItems = targetSheets.SelectMany(s => s.Items).ToList();

        // 4. Execute SIMD-accelerated matching
        var matchedPairs = await _matcher.MatchItemsAsync(allTargetItems, sourceItems, sensitivity, ct);

        // 5. Compute native currency summaries
        var currencySummaries = ComputeCurrencySummaries(sourceItems, matchedPairs);

        stopwatch.Stop();

        return new ReconciliationResult
        {
            MatchedPairs = matchedPairs,
            TargetSheets = targetSheets,
            SourceItems = sourceItems,
            CurrencySummaries = currencySummaries,
            ElapsedTime = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Executes intelligent multi-source reconciliation across multiple contractor pricing files,
    /// adhering to custom topological sheet link mappings and column routing channels.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileMultiSourceAsync(
        IReadOnlyList<string> sourceFilePaths,
        string targetFilePath,
        double sensitivity = 0.85,
        IReadOnlyList<SheetLinkMapping>? sheetMappings = null,
        ColumnMappingModel? columnMappings = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        // 1. Read Target Consultant workbook
        var targetSheetsTask = _hierarchicalReader.ReadConsultantHierarchicalBoqAsync(targetFilePath, ct);

        // 2. Read all Source workbooks concurrently
        var validSourcePaths = sourceFilePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (validSourcePaths.Count == 0)
        {
            throw new FileNotFoundException("No valid source contractor files found for reconciliation.");
        }

        var sourceTasks = validSourcePaths.Select(path => _flatReader.ReadContractorFlatBoqAsync(path, columnMappings, ct)).ToList();
        await Task.WhenAll(sourceTasks.Concat(new Task[] { targetSheetsTask })).ConfigureAwait(false);

        var targetSheets = await targetSheetsTask;
        var sourceLists = await Task.WhenAll(sourceTasks);
        var allSourceItems = sourceLists.SelectMany(list => list).ToList();

        // 3. Apply Sheet Link Mappings if specified
        var allTargetItems = targetSheets.SelectMany(s => s.Items).ToList();
        IReadOnlyList<BoqMatchedPair> matchedPairs;

        if (sheetMappings != null && sheetMappings.Count > 0)
        {
            // Build lookup of sheet link directives
            var linkLookup = sheetMappings.ToDictionary(m => m.TargetSheetName, StringComparer.OrdinalIgnoreCase);
            var pairsList = new List<BoqMatchedPair>(allTargetItems.Count);

            // Group target items by SheetName to isolate bills
            var targetsBySheet = allTargetItems.GroupBy(i => i.SheetName, StringComparer.OrdinalIgnoreCase);

            foreach (var sheetGroup in targetsBySheet)
            {
                string sheetName = sheetGroup.Key;
                var sheetItems = sheetGroup.ToList();

                if (linkLookup.TryGetValue(sheetName, out var mapping))
                {
                    if (mapping.Status == SheetLinkStatus.ShieldedPS)
                    {
                        foreach (var item in sheetItems)
                        {
                            pairsList.Add(new BoqMatchedPair
                            {
                                TargetItem = item with { Type = BoqItemType.ProvisionalSum },
                                MatchedSourceItem = null,
                                SimilarityScore = 1.0,
                                Confidence = MatchConfidence.Exact,
                                MatchRationale = "Provisional Sum: Contractually shielded from injection.",
                                IsApproved = true
                            });
                        }
                        continue;
                    }
                    else if (mapping.Status == SheetLinkStatus.Excluded)
                    {
                        foreach (var item in sheetItems)
                        {
                            pairsList.Add(new BoqMatchedPair
                            {
                                TargetItem = item with { Type = BoqItemType.VariationOrder },
                                MatchedSourceItem = null,
                                SimilarityScore = 0.0,
                                Confidence = MatchConfidence.Unmatched,
                                MatchRationale = "Excluded sheet from pricing scope.",
                                IsApproved = false
                            });
                        }
                        continue;
                    }

                    // Check if linked to a specific source sheet/table
                    string sourceTargetSheet = mapping.SelectedSourceSheet?.Trim() ?? string.Empty;
                    if (sourceTargetSheet.StartsWith("[") && sourceTargetSheet.Contains("] "))
                    {
                        sourceTargetSheet = sourceTargetSheet.Substring(sourceTargetSheet.IndexOf("] ") + 2).Trim();
                    }

                    bool isSpecificSource = !string.IsNullOrWhiteSpace(sourceTargetSheet) &&
                                            !sourceTargetSheet.StartsWith("[") &&
                                            !sourceTargetSheet.Contains("Global", StringComparison.OrdinalIgnoreCase) &&
                                            !sourceTargetSheet.Contains("عامة", StringComparison.OrdinalIgnoreCase) &&
                                            !sourceTargetSheet.Contains("شامل", StringComparison.OrdinalIgnoreCase);

                    if (isSpecificSource)
                    {
                        // Filter candidate source items strictly to the linked source table/bill
                        var scopedSources = allSourceItems.Where(s =>
                            string.Equals(s.BillNumber, sourceTargetSheet, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s.SheetName, sourceTargetSheet, StringComparison.OrdinalIgnoreCase)
                        ).ToList();

                        if (scopedSources.Count == 0)
                        {
                            // Fuzzy bill code matching (e.g. "02A" matches "Bill 02A-...")
                            string targetCode = mapping.TargetBillCode.Trim();
                            if (!string.IsNullOrEmpty(targetCode) && targetCode != "-")
                            {
                                scopedSources = allSourceItems.Where(s =>
                                    s.BillNumber.Contains(targetCode, StringComparison.OrdinalIgnoreCase) ||
                                    s.SheetName.Contains(targetCode, StringComparison.OrdinalIgnoreCase)
                                ).ToList();
                            }
                        }

                        if (scopedSources.Count > 0)
                        {
                            // Match sheet items against scoped source items
                            var scopedMatches = await _matcher.MatchItemsAsync(sheetItems, scopedSources, sensitivity, ct);

                            // Check if any items remain unmatched, and try global fallback for them
                            var unmatched = scopedMatches.Where(p => p.MatchedSourceItem == null).Select(p => p.TargetItem).ToList();
                            if (unmatched.Count > 0)
                            {
                                var fallbackMatches = await _matcher.MatchItemsAsync(unmatched, allSourceItems, sensitivity, ct);
                                var fallbackLookup = fallbackMatches.Where(p => p.MatchedSourceItem != null)
                                                                    .ToDictionary(p => p.TargetItem.Id, StringComparer.OrdinalIgnoreCase);

                                var updatedList = new List<BoqMatchedPair>(scopedMatches.Count);
                                foreach (var p in scopedMatches)
                                {
                                    if (p.MatchedSourceItem == null && fallbackLookup.TryGetValue(p.TargetItem.Id, out var fb))
                                    {
                                        updatedList.Add(fb);
                                    }
                                    else
                                    {
                                        updatedList.Add(p);
                                    }
                                }
                                pairsList.AddRange(updatedList);
                            }
                            else
                            {
                                pairsList.AddRange(scopedMatches);
                            }
                            continue;
                        }
                    }
                }

                // Default: Match against all source items
                var globalMatches = await _matcher.MatchItemsAsync(sheetItems, allSourceItems, sensitivity, ct);
                pairsList.AddRange(globalMatches);
            }

            matchedPairs = pairsList;
        }
        else
        {
            matchedPairs = await _matcher.MatchItemsAsync(allTargetItems, allSourceItems, sensitivity, ct);
        }

        // 4. Compute segregated currency summaries
        var currencySummaries = ComputeCurrencySummaries(allSourceItems, matchedPairs);

        stopwatch.Stop();

        return new ReconciliationResult
        {
            MatchedPairs = matchedPairs,
            TargetSheets = targetSheets,
            SourceItems = allSourceItems,
            CurrencySummaries = currencySummaries,
            ElapsedTime = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Executes batch multi-target reconciliation:
    /// Ingests N pricing/contractor source workbooks ONCE into an in-memory knowledge graph,
    /// reconciles against M consultant target workbooks,
    /// and exports M distinct reconciled workbooks preserving their respective structures, sheets, and formulas.
    /// </summary>
    public async Task<IReadOnlyList<BatchTargetResult>> ReconcileBatchMultiTargetAsync(
        IReadOnlyList<string> sourceFilePaths,
        IReadOnlyList<string> targetFilePaths,
        string outputDirectory,
        double sensitivity = 0.85,
        ColumnMappingModel? columnMappings = null,
        IProgress<(int current, int total, string targetName)>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var validSourcePaths = sourceFilePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var validTargetPaths = targetFilePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (validSourcePaths.Count == 0)
        {
            throw new FileNotFoundException("No valid source contractor files found for reconciliation.");
        }
        if (validTargetPaths.Count == 0)
        {
            throw new FileNotFoundException("No valid consultant target files found for reconciliation.");
        }

        Directory.CreateDirectory(outputDirectory);

        // Step 1: Ingest and index all N source workbooks once into memory
        var sourceTasks = validSourcePaths.Select(path => _flatReader.ReadContractorFlatBoqAsync(path, columnMappings, ct)).ToList();
        var sourceLists = await Task.WhenAll(sourceTasks).ConfigureAwait(false);
        var allSourceItems = sourceLists.SelectMany(list => list).ToList();

        var batchResults = new List<BatchTargetResult>(validTargetPaths.Count);
        string primarySourcePath = validSourcePaths[0];

        // Step 2: Reconcile each target workbook in high-speed sequence
        for (int i = 0; i < validTargetPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string targetPath = validTargetPaths[i];
            string targetName = Path.GetFileName(targetPath);

            progress?.Report((i + 1, validTargetPaths.Count, targetName));

            var sw = Stopwatch.StartNew();
            var targetSheets = await _hierarchicalReader.ReadConsultantHierarchicalBoqAsync(targetPath, ct).ConfigureAwait(false);
            var allTargetItems = targetSheets.SelectMany(s => s.Items).ToList();

            var matchedPairs = await _matcher.MatchItemsAsync(allTargetItems, allSourceItems, sensitivity, ct).ConfigureAwait(false);

            // Auto-approve valid matched pairs
            foreach (var p in matchedPairs)
            {
                if (p.InjectedRate.HasValue && !p.IsProvisionalSum && p.Confidence != MatchConfidence.ManualReviewNeeded)
                {
                    p.IsApproved = true;
                }
            }

            var currencySummaries = ComputeCurrencySummaries(allSourceItems, matchedPairs);
            sw.Stop();

            var result = new ReconciliationResult
            {
                MatchedPairs = matchedPairs,
                TargetSheets = targetSheets,
                SourceItems = allSourceItems,
                CurrencySummaries = currencySummaries,
                ElapsedTime = sw.Elapsed
            };

            // Export reconciled workbook
            string baseName = Path.GetFileNameWithoutExtension(targetPath);
            string outPath = Path.Combine(outputDirectory, $"{baseName}_Reconciled.xlsx");

            await _exporter.ExportPricedBoqAsync(targetPath, outPath, matchedPairs, primarySourcePath, enableDynamicLinking: true, progress: null, ct: ct).ConfigureAwait(false);

            batchResults.Add(new BatchTargetResult
            {
                TargetFilePath = targetPath,
                OutputFilePath = outPath,
                Result = result
            });
        }

        return batchResults;
    }


    /// <summary>
    /// Injects approved rates into the consultant Excel template and appends an audit report.
    /// Supports native OpenXML relative dynamic linking when contractor file path is provided.
    /// </summary>
    public Task ExportPricedScheduleAsync(
        string templatePath,
        string outputPath,
        IReadOnlyList<BoqMatchedPair> pairs,
        string? sourceContractorFilePath = null,
        bool enableDynamicLinking = true,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return _exporter.ExportPricedBoqAsync(templatePath, outputPath, pairs, sourceContractorFilePath, enableDynamicLinking, progress, ct);
    }

    /// <summary>
    /// Overload for backwards compatibility without dynamic linking.
    /// </summary>
    public Task ExportPricedScheduleAsync(
        string templatePath,
        string outputPath,
        IReadOnlyList<BoqMatchedPair> pairs,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return ExportPricedScheduleAsync(templatePath, outputPath, pairs, null, enableDynamicLinking: false, progress, ct);
    }

    /// <summary>
    /// Saves a reconciled snapshot into the local offline SQLite repository with invoice and file metadata.
    /// </summary>
    public async Task SaveSnapshotAsync(
        string projectCode,
        string revisionCode,
        IReadOnlyList<BoqMatchedPair> pairs,
        string? invoiceName = null,
        string? sourceFileName = null,
        string? targetFileName = null,
        string? exportFilePath = null,
        CancellationToken ct = default)
    {
        var approvedItems = pairs
            .Where(p => p.IsApproved && p.InjectedRate.HasValue)
            .Select(p => p.TargetItem with { UnitRate = p.InjectedRate, TotalAmount = p.InjectedRate * p.TargetItem.Quantity })
            .ToList();

        decimal totalEgp = approvedItems
            .Where(i => i.Currency.Equals("EGP", StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.TotalAmount ?? 0m);

        decimal totalUsd = approvedItems
            .Where(i => i.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.TotalAmount ?? 0m);

        var snapshot = new ProjectSnapshot
        {
            ProjectCode = projectCode,
            RevisionCode = revisionCode,
            InvoiceName = string.IsNullOrWhiteSpace(invoiceName) ? projectCode : invoiceName,
            SourceFileName = sourceFileName ?? string.Empty,
            TargetFileName = targetFileName ?? string.Empty,
            ExportFilePath = exportFilePath ?? string.Empty,
            SnapshotDate = DateTime.UtcNow,
            TotalValueEgp = totalEgp,
            TotalValueUsd = totalUsd,
            TotalItemsCount = approvedItems.Count,
            ContentHash = Guid.NewGuid().ToString("N")[..12]
        };

        await _repository.InitializeDatabaseAsync(ct);
        await _repository.SaveSnapshotAsync(snapshot, approvedItems, ct);
    }

    /// <summary>
    /// Explicitly initializes the underlying SQLite database schema and migrations.
    /// </summary>
    public Task InitializeDatabaseAsync(CancellationToken ct = default)
    {
        return _repository.InitializeDatabaseAsync(ct);
    }

    /// <summary>
    /// Queries historical rates from previous revisions and tender benchmarks.
    /// </summary>
    public Task<IReadOnlyList<BoqItem>> FindHistoricalRatesAsync(string normalizedDescription, string unit, CancellationToken ct = default)
    {
        return _repository.FindHistoricalRatesAsync(normalizedDescription, unit, ct);
    }

    /// <summary>
    /// Performs intelligent search over historical rates across invoice name, file name, item description, and codes.
    /// </summary>
    public Task<IReadOnlyList<HistoricalRateItem>> SearchHistoricalRatesAsync(string? searchTerm = null, int limit = 200, CancellationToken ct = default)
    {
        return _repository.SearchHistoricalRatesAsync(searchTerm, limit, ct);
    }

    public Task SaveMappingPresetAsync(MappingPreset preset, CancellationToken ct = default) =>
        _repository.SaveMappingPresetAsync(preset, ct);

    public Task<IReadOnlyList<MappingPreset>> GetMappingPresetsAsync(CancellationToken ct = default) =>
        _repository.GetMappingPresetsAsync(ct);

    public Task DeleteMappingPresetAsync(string presetName, CancellationToken ct = default) =>
        _repository.DeleteMappingPresetAsync(presetName, ct);

    public Task RecordAuditLogAsync(ItemAuditLog log, CancellationToken ct = default) =>
        _repository.RecordAuditLogAsync(log, ct);

    public Task<IReadOnlyList<ItemAuditLog>> GetAuditLogsForItemAsync(string itemId, CancellationToken ct = default) =>
        _repository.GetAuditLogsForItemAsync(itemId, ct);

    private static IReadOnlyList<CurrencyBucketSummary> ComputeCurrencySummaries(
        IReadOnlyList<BoqItem> sourceItems,
        IReadOnlyList<BoqMatchedPair> matchedPairs)
    {
        var currencies = matchedPairs
            .Select(p => p.TargetItem.Currency)
            .Concat(sourceItems.Select(s => s.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currencies.Count == 0)
        {
            currencies.Add("EGP");
        }

        var list = new List<CurrencyBucketSummary>(currencies.Count);

        foreach (var cur in currencies)
        {
            // Base amount from source items in this currency
            decimal baseTotal = sourceItems
                .Where(s => s.Currency.Equals(cur, StringComparison.OrdinalIgnoreCase) && s.TotalAmount.HasValue)
                .Sum(s => s.TotalAmount!.Value);

            // Re-measure amount from matched approved pairs
            decimal remeasureTotal = matchedPairs
                .Where(p => p.TargetItem.Currency.Equals(cur, StringComparison.OrdinalIgnoreCase) && 
                            p.IsApproved && 
                            p.InjectedRate.HasValue)
                .Sum(p => p.InjectedRate!.Value * p.TargetItem.Quantity);

            int itemsCount = matchedPairs.Count(p => p.TargetItem.Currency.Equals(cur, StringComparison.OrdinalIgnoreCase));

            list.Add(new CurrencyBucketSummary
            {
                Currency = cur,
                TotalBaseAmount = baseTotal,
                TotalRemeasureAmount = remeasureTotal,
                ItemsCount = itemsCount
            });
        }

        return list;
    }
}
