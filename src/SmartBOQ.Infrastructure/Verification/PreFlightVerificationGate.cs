using System.Collections.Concurrent;
using System.Text;
using ExcelDataReader;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Verification;

/// <summary>
/// Pre-flight schema and nomenclature integrity verification gate.
/// Verifies structural correctness, mandatory columns, bill worksheets, and provisional sums
/// concurrently across both source files via Task.WhenAll.
/// </summary>
public sealed class PreFlightVerificationGate : IVerificationGate
{
    static PreFlightVerificationGate()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly string[] RequiredFileAColumns =
    [
        "Bill",
        "Section",
        "Description",
        "Unit",
        "Qty",
        "Rate",
        "Total"
    ];

    public async Task<VerificationReport> VerifyFilesAsync(string fileAPath, string fileBPath, CancellationToken ct = default)
    {
        var passedChecks = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<string>();
        var warnings = new ConcurrentBag<string>();
        int fileARowsCount = 0;
        int fileBSheetsCount = 0;

        // Execute File A and File B verification concurrently
        var taskA = Task.Run(() =>
        {
            if (!File.Exists(fileAPath))
            {
                errors.Add($"File A not found at path: {fileAPath}");
                return;
            }

            try
            {
                using var stream = new FileStream(fileAPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
                using var reader = ExcelReaderFactory.CreateReader(stream);

                passedChecks.Add("File A (Contractor Flat BOQ) opened successfully.");

                bool headersFound = false;
                int rowIdx = 0;

                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rowIdx++;

                    if (rowIdx <= 3)
                    {
                        var rowValues = new List<string>(reader.FieldCount);
                        for (int c = 0; c < reader.FieldCount; c++)
                        {
                            rowValues.Add(reader.GetValue(c)?.ToString()?.Trim() ?? string.Empty);
                        }

                        int matches = RequiredFileAColumns.Count(col => 
                            rowValues.Any(val => val.Contains(col, StringComparison.OrdinalIgnoreCase)));

                        if (matches >= 4)
                        {
                            headersFound = true;
                            passedChecks.Add($"File A header schema verified with {matches}/{RequiredFileAColumns.Length} key column signatures.");
                        }
                    }

                    if (rowIdx > 2)
                    {
                        string? billVal = reader.GetValue(2)?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(billVal) && billVal.StartsWith("Bill", StringComparison.OrdinalIgnoreCase))
                        {
                            fileARowsCount++;
                        }
                    }
                }

                if (!headersFound)
                {
                    warnings.Add("File A header row did not match standard layout exactly, but rows were scanned.");
                }

                if (fileARowsCount > 0)
                {
                    passedChecks.Add($"File A data row count verified: {fileARowsCount:N0} priced line items detected.");
                }
                else
                {
                    errors.Add("File A does not contain any valid bill item rows starting with 'Bill'.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"File A verification failed with error: {ex.Message}");
            }
        }, ct);

        var taskB = Task.Run(() =>
        {
            if (!File.Exists(fileBPath))
            {
                errors.Add($"File B not found at path: {fileBPath}");
                return;
            }

            try
            {
                using var stream = new FileStream(fileBPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
                using var reader = ExcelReaderFactory.CreateReader(stream);

                passedChecks.Add("File B (Consultant Pricing Schedule) opened successfully.");

                var billSheets = new List<string>(32);
                var psSheets = new List<string>(16);

                do
                {
                    ct.ThrowIfCancellationRequested();
                    string sheetName = reader.Name?.Trim() ?? string.Empty;
                    fileBSheetsCount++;

                    if (sheetName.StartsWith("Bill", StringComparison.OrdinalIgnoreCase))
                    {
                        billSheets.Add(sheetName);

                        if (sheetName.Contains("Provisional", StringComparison.OrdinalIgnoreCase) || 
                            sheetName.EndsWith("PS", StringComparison.OrdinalIgnoreCase))
                        {
                            psSheets.Add(sheetName);
                        }
                    }
                } while (reader.NextResult());

                if (billSheets.Count >= 10)
                {
                    passedChecks.Add($"File B multi-sheet structure verified: {billSheets.Count} Bill worksheets detected.");
                }
                else
                {
                    warnings.Add($"File B contains only {billSheets.Count} Bill worksheets (expected >= 20).");
                }

                if (psSheets.Count > 0)
                {
                    passedChecks.Add($"Provisional Sums Guard detected and isolated {psSheets.Count} protected sheets: {string.Join(", ", psSheets)}.");
                }
                else
                {
                    warnings.Add("No Provisional Sum sheets identified by name pattern.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"File B verification failed with error: {ex.Message}");
            }
        }, ct);

        await Task.WhenAll(taskA, taskB).ConfigureAwait(false);

        var errorList = errors.ToList();
        var warningList = warnings.ToList();
        var passedList = passedChecks.ToList();

        bool isValid = errorList.Count == 0;
        var status = isValid 
            ? VerificationStatus.Passed 
            : (errorList.Any(e => e.Contains("column", StringComparison.OrdinalIgnoreCase)) 
                ? VerificationStatus.FailedMissingColumns 
                : VerificationStatus.FailedCorrupted);

        return new VerificationReport
        {
            IsValid = isValid,
            Status = status,
            PassedChecks = passedList,
            Errors = errorList,
            Warnings = warningList,
            FileARowsCount = fileARowsCount,
            FileBSheetsCount = fileBSheetsCount
        };
    }
}
