using SmartBOQ.Domain.Models;

namespace SmartBOQ.Domain.Interfaces;

/// <summary>
/// Forward-only zero-allocation streaming Excel reader contract.
/// </summary>
public interface IBoqReader
{
    /// <summary>
    /// Reads contractor flat tabular database (e.g. Hatchway 2,030 rows).
    /// </summary>
    Task<IReadOnlyList<BoqItem>> ReadContractorFlatBoqAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Reads consultant hierarchical multi-sheet workbook and reconstructs multi-row items via FSM.
    /// </summary>
    Task<IReadOnlyList<BoqSheet>> ReadConsultantHierarchicalBoqAsync(string filePath, CancellationToken ct = default);
}

/// <summary>
/// Pre-flight schema and nomenclature integrity verification gate contract.
/// </summary>
public interface IVerificationGate
{
    Task<VerificationReport> VerifyFilesAsync(string fileAPath, string fileBPath, CancellationToken ct = default);
}

/// <summary>
/// SIMD-accelerated hybrid weighted item matching engine contract.
/// </summary>
public interface IItemMatcher
{
    Task<IReadOnlyList<BoqMatchedPair>> MatchItemsAsync(
        IReadOnlyList<BoqItem> targetItems,
        IReadOnlyList<BoqItem> sourceItems,
        double sensitivity = 0.85,
        CancellationToken ct = default);
}

/// <summary>
/// Identifies target anchor row index where formula and quantity reside.
/// </summary>
public interface IAnchorRowResolver
{
    int ResolveAnchorRow(IReadOnlyList<int> itemRowIndices, Func<int, bool> hasQuantity, Func<int, bool> hasAmountFormula);
}

/// <summary>
/// Template-safe Excel rate injection and formula preservation exporter contract.
/// </summary>
public interface IBoqExporter
{
    Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath,
        bool enableDynamicLinking = true,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Embedded local SQLite repository contract for snapshots and historical rate benchmarks.
/// </summary>
public interface ISqliteRepository
{
    Task InitializeDatabaseAsync(CancellationToken ct = default);
    Task SaveSnapshotAsync(ProjectSnapshot snapshot, IReadOnlyList<BoqItem> items, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectSnapshot>> GetSnapshotsAsync(string projectCode, CancellationToken ct = default);
    Task<IReadOnlyList<BoqItem>> FindHistoricalRatesAsync(string normalizedDescription, string unit, CancellationToken ct = default);
}

/// <summary>
/// Decoupled localization and nomenclature contract.
/// </summary>
public interface ILocalizationService
{
    string CurrentCulture { get; }
    string GetString(string key);
    string this[string key] { get; }
    void SetCulture(string cultureCode); // e.g. "ar-EG" or "en-US"
}

/// <summary>
/// Crash diagnostics and daily file logging contract.
/// Automatically creates daily log files in the runtime directory upon errors or crashes.
/// </summary>
public interface IAppLogger
{
    string LogDirectoryPath { get; }
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
    void LogCrash(Exception ex, string context = "");
}
