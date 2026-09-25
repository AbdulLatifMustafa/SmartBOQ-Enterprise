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

    public BoqReconciliationService(
        IVerificationGate verificationGate,
        IBoqReader flatReader,
        IBoqReader hierarchicalReader,
        IItemMatcher matcher,
        IBoqExporter exporter,
        ISqliteRepository repository)
    {
        _verificationGate = verificationGate;
        _flatReader = flatReader;
        _hierarchicalReader = hierarchicalReader;
        _matcher = matcher;
        _exporter = exporter;
        _repository = repository;
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

        // 5. Compute segregated currency summaries (Strict native currency fidelity - Zero FX blending)
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
    /// Saves a reconciled snapshot into the local offline SQLite repository.
    /// </summary>
    public async Task SaveSnapshotAsync(
        string projectCode,
        string revisionCode,
        IReadOnlyList<BoqMatchedPair> pairs,
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
    /// Queries historical rates from previous revisions and tender benchmarks.
    /// </summary>
    public Task<IReadOnlyList<BoqItem>> FindHistoricalRatesAsync(string normalizedDescription, string unit, CancellationToken ct = default)
    {
        return _repository.FindHistoricalRatesAsync(normalizedDescription, unit, ct);
    }

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
