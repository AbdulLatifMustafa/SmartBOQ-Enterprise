using System.Globalization;
using System.IO;
using System.Text;
using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Logging;

/// <summary>
/// Diagnostic logger generating comprehensive, human-readable execution reports
/// for every reconciliation and export session into the Log folder.
/// Captures file manifests, anomaly recoveries, match metrics, and error diagnostics.
/// </summary>
public static class ReconciliationDiagnosticLogger
{
    private static readonly Lock LogLock = new();

    /// <summary>
    /// Generates and writes an execution and diagnostic text report to the Log directory.
    /// Returns the absolute path of the generated report file.
    /// </summary>
    public static string WriteDiagnosticReport(
        string targetFilePath,
        IReadOnlyList<string> sourceFilePaths,
        ReconciliationResult? result,
        string? exportOutputPath = null,
        string? dashboardOutputPath = null,
        string? snapshotRevisionId = null,
        Exception? exception = null,
        string? customLogDir = null)
    {
        string logDir = !string.IsNullOrWhiteSpace(customLogDir)
            ? customLogDir
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log");

        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        string baseName = !string.IsNullOrWhiteSpace(targetFilePath) && File.Exists(targetFilePath)
            ? Path.GetFileNameWithoutExtension(targetFilePath)
            : "BOQ_Reconciliation";

        string statusTag = exception != null ? "ERROR" : (result?.MatchedPairs.Any(p => p.Confidence == MatchConfidence.ManualReviewNeeded) == true ? "WARNING" : "SUCCESS");
        string fileName = $"Reconciliation_{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}_{statusTag}.txt";
        string fullPath = Path.Combine(logDir, fileName);

        var sb = new StringBuilder(4096);
        string border = new('=', 82);
        string subBorder = new('-', 82);

        sb.AppendLine(border);
        sb.AppendLine("SMARTBOQ ENTERPRISE - RECONCILIATION & MERGE DIAGNOSTIC REPORT");
        sb.AppendLine("تقرير تشخيص ومطابقة المقايسات الهندسية وكشف الأخطاء الذكي");
        sb.AppendLine(border);
        sb.AppendLine($"Timestamp (Local) : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Timestamp (UTC)   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Host Environment  : {Environment.MachineName} ({Environment.OSVersion}, {(Environment.Is64BitProcess ? "64-bit" : "32-bit")})");
        sb.AppendLine($"Process WorkingSet: {Environment.WorkingSet / (1024 * 1024):N1} MB");
        sb.AppendLine($"Engine Build      : SmartBOQ Enterprise v2.0 (.NET 10.0)");
        sb.AppendLine();

        // 1. FILE MANIFEST
        sb.AppendLine("[1] FILE MANIFEST & INPUT SOURCES");
        sb.AppendLine(subBorder);
        sb.AppendLine($"Consultant Target BOQ (File B):");
        sb.AppendLine($"  Path   : {targetFilePath}");
        if (File.Exists(targetFilePath))
        {
            var fi = new FileInfo(targetFilePath);
            sb.AppendLine($"  Size   : {fi.Length / 1024.0:N1} KB | Last Modified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            sb.AppendLine($"  Status : WARNING - File not accessible or missing on disk.");
        }

        sb.AppendLine($"Contractor Source BOQs (File A & Candidates) [{sourceFilePaths.Count}]:");
        for (int i = 0; i < sourceFilePaths.Count; i++)
        {
            string srcPath = sourceFilePaths[i];
            sb.AppendLine($"  [{i + 1}] {srcPath}");
            if (File.Exists(srcPath))
            {
                var fi = new FileInfo(srcPath);
                sb.AppendLine($"      Size: {fi.Length / 1024.0:N1} KB | Last Modified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                sb.AppendLine($"      Status: WARNING - File not accessible.");
            }
        }
        sb.AppendLine();

        // 2. EXCEPTION & ERROR DIAGNOSTICS (IF APPLICABLE)
        if (exception != null)
        {
            sb.AppendLine("[!] ERROR & EXCEPTION DIAGNOSTICS");
            sb.AppendLine(subBorder);
            sb.AppendLine($"Exception Type   : {exception.GetType().FullName}");
            sb.AppendLine($"Message          : {exception.Message}");
            sb.AppendLine($"Target Method    : {exception.TargetSite}");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(exception.StackTrace);

            if (exception.InnerException != null)
            {
                sb.AppendLine($"Inner Exception  : {exception.InnerException.GetType().FullName}: {exception.InnerException.Message}");
                sb.AppendLine(exception.InnerException.StackTrace);
            }

            sb.AppendLine();
            sb.AppendLine("Smart Troubleshooting Recommendations:");
            if (exception is IOException ioEx && ioEx.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("  * The file is currently opened in Excel or WPS. Close all spreadsheets and re-run.");
            }
            else if (exception is FileNotFoundException)
            {
                sb.AppendLine("  * Verify that source/target Excel files exist at the specified path and have read permissions.");
            }
            else
            {
                sb.AppendLine("  * Check sheet headers structure. Ensure column titles are located within the top 35 rows.");
                sb.AppendLine("  * Review custom column channel mappings under the 'Compare Pipeline' tab.");
            }
            sb.AppendLine();
        }

        // 3. RECONCILIATION & MATCHING METRICS
        if (result != null)
        {
            int total = result.TotalTargetItems;
            int exactCount = result.MatchedPairs.Count(p => p.Confidence == MatchConfidence.Exact);
            int highCount = result.MatchedPairs.Count(p => p.Confidence == MatchConfidence.HighFuzzy);
            int reviewCount = result.MatchedPairs.Count(p => p.Confidence == MatchConfidence.ManualReviewNeeded);
            int unmatchedCount = result.MatchedPairs.Count(p => p.Confidence == MatchConfidence.Unmatched);
            int shieldedPsCount = result.MatchedPairs.Count(p => p.IsProvisionalSum);
            int voCount = result.MatchedPairs.Count(p => p.IsVariationOrder);
            int approvedCount = result.MatchedPairs.Count(p => p.IsApproved);

            double exactPct = total > 0 ? (exactCount * 100.0 / total) : 0;
            double highPct = total > 0 ? (highCount * 100.0 / total) : 0;
            double reviewPct = total > 0 ? (reviewCount * 100.0 / total) : 0;
            double unmatchedPct = total > 0 ? (unmatchedCount * 100.0 / total) : 0;

            sb.AppendLine("[2] RECONCILIATION ACCURACY & CONFIDENCE METRICS");
            sb.AppendLine(subBorder);
            sb.AppendLine($"Total Consultant Target Items   : {total:N0}");
            sb.AppendLine($"Contractor Source Items Scanned : {result.SourceItems.Count:N0}");
            sb.AppendLine($"Consultant Sheets Discovered    : {result.TargetSheets.Count:N0}");
            sb.AppendLine($"Elapsed Execution Time          : {result.ElapsedTime.TotalSeconds:F2} seconds");
            double throughput = result.ElapsedTime.TotalSeconds > 0 ? total / result.ElapsedTime.TotalSeconds : 0;
            sb.AppendLine($"Processing Throughput           : {throughput:N0} items/sec");
            sb.AppendLine();
            sb.AppendLine($"Match Confidence Breakdown:");
            sb.AppendLine($"  - Exact Matches (100% Score)   : {exactCount,6:N0} ({exactPct:F1}%)  [Code / Full Semantic Match]");
            sb.AppendLine($"  - High Confidence (>= 85%)     : {highCount,6:N0} ({highPct:F1}%)  [SIMD Levenshtein & Jaccard Match]");
            sb.AppendLine($"  - Manual Review Needed (< 85%) : {reviewCount,6:N0} ({reviewPct:F1}%)  [Low Confidence - Requires Verification]");
            sb.AppendLine($"  - Unmatched / Unpriced (0%)    : {unmatchedCount,6:N0} ({unmatchedPct:F1}%)  [No Matching Source Item Found]");
            sb.AppendLine($"  - Contractually Shielded (PS)  : {shieldedPsCount,6:N0}         [Provisional Sums Preserved]");
            sb.AppendLine($"  - Variation Orders (VO)        : {voCount,6:N0}         [Non-tender Scope Isolated]");
            sb.AppendLine($"  - Total Approved for Injection : {approvedCount,6:N0}");
            sb.AppendLine();

            // 4. MULTI-CURRENCY FINANCIAL ANALYSIS
            sb.AppendLine("[3] MULTI-CURRENCY SEGREGATION & FINANCIAL SUMMARY");
            sb.AppendLine(subBorder);
            sb.AppendLine("Strict Currency Fidelity: Zero FX blending or speculative exchange conversion.");
            foreach (var bucket in result.CurrencySummaries)
            {
                decimal variance = bucket.TotalRemeasureAmount - bucket.TotalBaseAmount;
                decimal variancePct = bucket.TotalBaseAmount > 0 ? (variance / bucket.TotalBaseAmount) * 100m : 0m;
                string sign = variance >= 0 ? "+" : "";

                sb.AppendLine($"Currency: {bucket.Currency}");
                sb.AppendLine($"  Contractor Base Total    : {bucket.TotalBaseAmount:N2} {bucket.Currency}");
                sb.AppendLine($"  Re-measured Target Total : {bucket.TotalRemeasureAmount:N2} {bucket.Currency}");
                sb.AppendLine($"  Net Financial Variance   : {sign}{variance:N2} {bucket.Currency} ({sign}{variancePct:F2}%)");
                sb.AppendLine($"  Active Items in Currency : {bucket.ItemsCount:N0}");
                sb.AppendLine();
            }

            // 5. ANOMALY RECOVERY AUDIT
            sb.AppendLine("[4] RESILIENT RECOVERY & ANOMALY LOG");
            sb.AppendLine(subBorder);
            sb.AppendLine("Algorithmic Ingestion Normalizations Executed:");
            sb.AppendLine("  * Numeral Formatting   : Eastern Arabic (٠-٩) and Persian digits automatically converted to European (0-9).");
            sb.AppendLine("  * Comma Decimal Parsing: European decimal comma formats (e.g. 125,50) auto-detected and normalized.");
            sb.AppendLine("  * Currency Token Strip : Cleaned symbols (EGP, USD, EUR, ج.م, $, etc.) without altering unit rates.");
            sb.AppendLine("  * Formula Error Shields: Excel formula tokens (#VALUE!, #REF!, #DIV/0!, #N/A, 'Rate only', 'بند محمل')");
            sb.AppendLine("                           safely bypassed and isolated without terminating evaluation.");
            sb.AppendLine();

            // 6. TOP ATTENTION ITEMS
            var attentionItems = result.MatchedPairs
                .Where(p => p.Confidence == MatchConfidence.ManualReviewNeeded || (!p.InjectedRate.HasValue && !p.IsProvisionalSum))
                .Take(20)
                .ToList();

            if (attentionItems.Count > 0)
            {
                sb.AppendLine($"[5] TOP ANOMALIES & ITEMS REQUIRING ATTENTION (Showing top {attentionItems.Count}):");
                sb.AppendLine(subBorder);
                for (int i = 0; i < attentionItems.Count; i++)
                {
                    var item = attentionItems[i];
                    string code = !string.IsNullOrWhiteSpace(item.TargetItem.ItemCode) ? item.TargetItem.ItemCode : "N/A";
                    string desc = item.TargetItem.Description.Replace('\r', ' ').Replace('\n', ' ');
                    if (desc.Length > 60) desc = desc[..57] + "...";

                    sb.AppendLine($"  [{i + 1}] Code: {code,-15} | Sheet: {item.TargetItem.SheetName,-15} | Confidence: {item.Confidence}");
                    sb.AppendLine($"      Desc  : {desc}");
                    sb.AppendLine($"      Reason: {item.MatchRationale}");
                    sb.AppendLine();
                }
            }
        }

        // 7. OUTPUT ARTIFACTS
        sb.AppendLine("[6] OUTPUT ARTIFACTS & AUDIT PERSISTENCE");
        sb.AppendLine(subBorder);
        sb.AppendLine($"Reconciled BOQ Excel File : {exportOutputPath ?? "Pending or Not Exported"}");
        sb.AppendLine($"Executive Dashboard File  : {dashboardOutputPath ?? "Pending or Not Exported"}");
        sb.AppendLine($"SQLite Snapshot Revision  : {snapshotRevisionId ?? "Local Offline Store"}");
        sb.AppendLine($"Diagnostic Log File Path  : {fullPath}");
        sb.AppendLine();

        sb.AppendLine(border);
        sb.AppendLine($"STATUS: {(exception != null ? "FAILED WITH EXCEPTION" : "SUCCESSFULLY GENERATED")}");
        sb.AppendLine(border);

        string content = sb.ToString();

        lock (LogLock)
        {
            File.WriteAllText(fullPath, content, Encoding.UTF8);
        }

        return fullPath;
    }
}
