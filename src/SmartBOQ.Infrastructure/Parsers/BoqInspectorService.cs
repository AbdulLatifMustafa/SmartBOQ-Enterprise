using System.Text;
using ExcelDataReader;
using Fastenshtein;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Parsers;

/// <summary>
/// High-speed workbook structure inspector, column detector, and topological table linker.
/// Analyzes Excel files in milliseconds without loading full data models into RAM.
/// </summary>
public sealed class BoqInspectorService : IBoqInspector
{
    static BoqInspectorService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly string[] NonBillSheetPrefixes =
    [
        "TABLE OF CONTENTS", "PREAMBLE", "SCHEDULE OF INSURANCE",
        "INSTRUCTION", "GRAND SUMMARY", "COVER", "DAYWORKS",
        "PRICE ANALYSIS", "AUDIT", "DASHBOARD", "EXECUTIVE",
        "PRICING_LINKAGE", "LINKAGE"
    ];

    public async Task<BoqFileInfo> InspectWorkbookAsync(string filePath, BoqFileRole role = BoqFileRole.ContractorPriced, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Excel file not found for inspection.", filePath);
        }

        return await Task.Run(() =>
        {
            var fileInfo = new FileInfo(filePath);
            var sheetSummaries = new List<BoqSheetSummary>(32);
            var columnHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalEstimatedItems = 0;
            string detectedCurrency = "EGP";

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            if (reader.ResultsCount == 1)
            {
                // Single-sheet flat BOQ schedule: resolve schema dynamically using AI semantic resolver
                var resolvedCols = SemanticColumnResolver.ResolveColumns(reader, maxScanRows: 30);
                detectedCurrency = resolvedCols.DetectedCurrency;

                // Reset stream to re-scan for distinct Bill partitions
                stream.Seek(0, SeekOrigin.Begin);
                using var flatReader = ExcelReaderFactory.CreateReader(stream);

                var billRowCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var billStartRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int colBill = resolvedCols.BillColumn;
                int rowIdx = 0;
                string physicalName = string.IsNullOrWhiteSpace(flatReader.Name) ? "Sheet1" : flatReader.Name.Trim();

                while (flatReader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rowIdx++;

                    if (rowIdx <= resolvedCols.HeaderRowIndex)
                    {
                        for (int c = 0; c < flatReader.FieldCount; c++)
                        {
                            var h = flatReader.GetValue(c)?.ToString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(h)) columnHeaders.Add(h);
                        }
                        continue;
                    }

                    string billVal = colBill >= 0 ? (flatReader.GetValue(colBill)?.ToString()?.Trim() ?? string.Empty) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(billVal))
                    {
                        billRowCounts[billVal] = billRowCounts.GetValueOrDefault(billVal) + 1;
                        if (!billStartRows.ContainsKey(billVal))
                        {
                            billStartRows[billVal] = rowIdx;
                        }
                        totalEstimatedItems++;
                    }
                }

                if (billRowCounts.Count > 0)
                {
                    foreach (var (billName, count) in billRowCounts)
                    {
                        sheetSummaries.Add(new BoqSheetSummary
                        {
                            SheetName = billName,
                            BillCode = ExtractBillCode(billName),
                            EstimatedRows = count,
                            StartRowIndex = billStartRows.GetValueOrDefault(billName, 1),
                            IsProvisionalSum = billName.Equals("Bill 6 Provisional Sum", StringComparison.OrdinalIgnoreCase) ||
                                             billName.StartsWith("Bill 6 Provisional", StringComparison.OrdinalIgnoreCase) ||
                                             billName.Contains("Provisional Sum", StringComparison.OrdinalIgnoreCase) || 
                                             billName.Contains("مبلغ احتياطي", StringComparison.OrdinalIgnoreCase) || 
                                             billName.Contains("مبالغ احتياطية", StringComparison.OrdinalIgnoreCase),
                            IsNonBillSheet = false,
                            DetectedCurrency = detectedCurrency,
                            SourceFileName = fileInfo.Name
                        });
                    }
                }
                else
                {
                    sheetSummaries.Add(new BoqSheetSummary
                    {
                        SheetName = physicalName,
                        BillCode = ExtractBillCode(physicalName),
                        EstimatedRows = Math.Max(0, rowIdx - resolvedCols.HeaderRowIndex),
                        IsProvisionalSum = false,
                        IsNonBillSheet = false,
                        DetectedCurrency = detectedCurrency,
                        SourceFileName = fileInfo.Name
                    });
                    totalEstimatedItems = Math.Max(0, rowIdx - resolvedCols.HeaderRowIndex);
                }
            }
            else
            {
                // Multi-sheet workbook: iterate each sheet
                do
                {
                    ct.ThrowIfCancellationRequested();
                    string sheetName = reader.Name?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(sheetName)) continue;

                    bool isNonBill = IsNonBillSheet(sheetName);
                    bool isPsSheet = sheetName.Equals("Bill 6 Provisional Sum", StringComparison.OrdinalIgnoreCase) ||
                                     sheetName.StartsWith("Bill 6 Provisional", StringComparison.OrdinalIgnoreCase) ||
                                     sheetName.Contains("Provisional Sum", StringComparison.OrdinalIgnoreCase) ||
                                     sheetName.Contains("مبلغ احتياطي", StringComparison.OrdinalIgnoreCase) ||
                                     sheetName.Contains("مبالغ احتياطية", StringComparison.OrdinalIgnoreCase);

                    int sheetRows = 0;
                    int rowLimit = 60; // Scan up to 60 rows for quick estimation
                    int scannedRows = 0;

                    while (reader.Read())
                    {
                        scannedRows++;
                        bool hasData = false;

                        for (int c = 0; c < reader.FieldCount; c++)
                        {
                            var cellVal = reader.GetValue(c)?.ToString()?.Trim();
                            if (string.IsNullOrWhiteSpace(cellVal)) continue;

                            hasData = true;

                            // Header detection in the first 5 rows
                            if (scannedRows <= 5 && cellVal.Length <= 40)
                            {
                                columnHeaders.Add(cellVal);
                            }

                            if (cellVal.Contains("USD", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("($)"))
                            {
                                detectedCurrency = "USD";
                            }
                            else if (cellVal.Contains("EUR", StringComparison.OrdinalIgnoreCase) || cellVal.Contains("(€)"))
                            {
                                detectedCurrency = "EUR";
                            }
                        }

                        if (hasData) sheetRows++;
                        if (scannedRows >= rowLimit) break;
                    }

                    // If scanned full rows or reached limit, estimate full sheet count
                    int estimatedCount = isNonBill ? 0 : (scannedRows >= rowLimit ? Math.Max(sheetRows * 5, 120) : sheetRows);
                    totalEstimatedItems += estimatedCount;

                    sheetSummaries.Add(new BoqSheetSummary
                    {
                        SheetName = sheetName,
                        BillCode = ExtractBillCode(sheetName),
                        EstimatedRows = estimatedCount,
                        IsProvisionalSum = isPsSheet,
                        IsNonBillSheet = isNonBill,
                        DetectedCurrency = detectedCurrency,
                        SourceFileName = fileInfo.Name
                    });

                } while (reader.NextResult());
            }


            return new BoqFileInfo
            {
                FilePath = filePath,
                FileName = fileInfo.Name,
                FileSizeBytes = fileInfo.Length,
                Role = role,
                SheetsCount = sheetSummaries.Count,
                TotalEstimatedItems = totalEstimatedItems,
                DetectedCurrency = detectedCurrency,
                Sheets = sheetSummaries,
                AvailableColumns = columnHeaders.OrderBy(c => c).ToList(),
                IsPrimary = role == BoqFileRole.ConsultantTarget || role == BoqFileRole.ContractorPriced
            };
        }, ct);
    }

    public async Task<IReadOnlyList<SheetLinkMapping>> AutoLinkSheetsAsync(
        IReadOnlyList<BoqSheetSummary> targetSheets,
        IReadOnlyList<BoqSheetSummary> sourceSheets,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var mappings = new List<SheetLinkMapping>(targetSheets.Count);

            foreach (var target in targetSheets)
            {
                ct.ThrowIfCancellationRequested();

                // Skip non-bill summary sheets from mapping
                if (target.IsNonBillSheet) continue;

                // Rule 1: Fuzzy topological match against available source sheets
                BoqSheetSummary? bestMatch = null;
                double bestScore = 0.0;

                string targetNorm = NormalizeName(target.SheetName);
                string targetCode = target.BillCode.Trim();

                if (sourceSheets.Count > 0)
                {
                    foreach (var src in sourceSheets)
                    {
                        if (src.IsNonBillSheet) continue;

                        double score = 0.0;
                        string srcNorm = NormalizeName(src.SheetName);
                        string srcCode = src.BillCode.Trim();

                        // Direct exact sheet name match
                        if (string.Equals(target.SheetName.Trim(), src.SheetName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            score = 1.0;
                        }
                        // Direct bill code alignment (e.g., 02A with 02A, 06.1J with 06.1J, 2A with 02A)
                        else if (!string.IsNullOrEmpty(targetCode) && !string.IsNullOrEmpty(srcCode) &&
                                 targetCode != "-" && srcCode != "-" &&
                                 (string.Equals(targetCode, srcCode, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(targetCode.TrimStart('0'), srcCode.TrimStart('0'), StringComparison.OrdinalIgnoreCase)))
                        {
                            score = 1.0;
                        }
                        else if (targetNorm.Equals(srcNorm, StringComparison.OrdinalIgnoreCase))
                        {
                            score = 1.0;
                        }
                        else
                        {
                            // Levenshtein & token match
                            int maxLen = Math.Max(targetNorm.Length, srcNorm.Length);
                            if (maxLen > 0)
                            {
                                int dist = Levenshtein.Distance(targetNorm, srcNorm);
                                double levSim = 1.0 - ((double)dist / maxLen);

                                double jaccard = ComputeJaccard(targetNorm, srcNorm);
                                score = (levSim * 0.6) + (jaccard * 0.4);
                            }
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestMatch = src;
                        }
                    }
                }

                // If candidate source sheet matched with high confidence, auto-link it!
                if (bestMatch != null && bestScore >= 0.70)
                {
                    mappings.Add(new SheetLinkMapping
                    {
                        TargetSheetName = target.SheetName,
                        TargetBillCode = target.BillCode,
                        TargetItemCount = target.EstimatedRows,
                        TargetStartRow = target.StartRowIndex,
                        SourceStartRow = bestMatch.StartRowIndex,
                        IsProvisionalSum = false,
                        SelectedSourceFile = bestMatch.SourceFileName,
                        SelectedSourceSheet = bestMatch.SheetName,
                        MatchScore = bestScore,
                        Status = SheetLinkStatus.AutoMatched
                    });
                }
                // If NO source sheet matches AND target is a contractually shielded Provisional Sum schedule
                else if (target.IsProvisionalSum)
                {
                    mappings.Add(new SheetLinkMapping
                    {
                        TargetSheetName = target.SheetName,
                        TargetBillCode = target.BillCode,
                        TargetItemCount = target.EstimatedRows,
                        TargetStartRow = target.StartRowIndex,
                        SourceStartRow = 1,
                        IsProvisionalSum = true,
                        SelectedSourceFile = "-",
                        SelectedSourceSheet = "محمي تلقائياً (PS Shielded)",
                        MatchScore = 1.0,
                        Status = SheetLinkStatus.ShieldedPS
                    });
                }
                else if (sourceSheets.Count == 0)
                {
                    mappings.Add(new SheetLinkMapping
                    {
                        TargetSheetName = target.SheetName,
                        TargetBillCode = target.BillCode,
                        TargetItemCount = target.EstimatedRows,
                        TargetStartRow = target.StartRowIndex,
                        SourceStartRow = 1,
                        IsProvisionalSum = false,
                        SelectedSourceFile = string.Empty,
                        SelectedSourceSheet = "[مطابقة عامة في كامل المقايسة]",
                        MatchScore = 1.0,
                        Status = SheetLinkStatus.GlobalSearch
                    });
                }
                else
                {
                    // Fallback to Global Cross-Sheet Search
                    mappings.Add(new SheetLinkMapping
                    {
                        TargetSheetName = target.SheetName,
                        TargetBillCode = target.BillCode,
                        TargetItemCount = target.EstimatedRows,
                        TargetStartRow = target.StartRowIndex,
                        SourceStartRow = 1,
                        IsProvisionalSum = false,
                        SelectedSourceFile = sourceSheets.FirstOrDefault()?.SourceFileName ?? string.Empty,
                        SelectedSourceSheet = "[مطابقة عامة في كامل المقايسة]",
                        MatchScore = 0.85,
                        Status = SheetLinkStatus.GlobalSearch
                    });
                }

            }

            return (IReadOnlyList<SheetLinkMapping>)mappings;
        }, ct);
    }

    public async Task<ColumnMappingModel> DetectColumnMappingAsync(string sourceFilePath, string targetFilePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var mapping = new ColumnMappingModel();

            // Default fallback defaults:
            // Contractor flat (Hatchway DP3): Col 17 (R: Rate), Col 11 (L: Desc), Col 10 (K: Code), Col 14 (O: Qty), Col 13 (N: Unit)
            // Consultant hierarchical (REH): Col 6 (G: Rate), Col 2 (C: Desc), Col 0 (A: Code), Col 4 (E: Qty), Col 5 (F: Unit)

            try
            {
                if (File.Exists(sourceFilePath))
                {
                    using var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32768, FileOptions.SequentialScan);
                    using var reader = ExcelReaderFactory.CreateReader(stream);
                    var srcCols = SemanticColumnResolver.ResolveColumns(reader, maxScanRows: 35);

                    if (srcCols.RateColumn >= 0) mapping.SourceRateColumn = srcCols.RateColumn;
                    if (srcCols.DescriptionColumn >= 0) mapping.SourceDescColumn = srcCols.DescriptionColumn;
                    if (srcCols.ItemCodeColumn >= 0) mapping.SourceCodeColumn = srcCols.ItemCodeColumn;
                    if (srcCols.QuantityColumn >= 0) mapping.SourceQtyColumn = srcCols.QuantityColumn;
                    if (srcCols.UnitColumn >= 0) mapping.SourceUnitColumn = srcCols.UnitColumn;
                }

                if (File.Exists(targetFilePath))
                {
                    using var stream = new FileStream(targetFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32768, FileOptions.SequentialScan);
                    using var reader = ExcelReaderFactory.CreateReader(stream);

                    // Multi-sheet consultant schedule: advance to the first actual work bill sheet with detailed line items
                    do
                    {
                        string sName = reader.Name?.Trim() ?? string.Empty;
                        bool isSummaryOrPrelim = sName.Contains("Sum", StringComparison.OrdinalIgnoreCase) ||
                                                 sName.Contains("General", StringComparison.OrdinalIgnoreCase) ||
                                                 sName.Contains("Requirements", StringComparison.OrdinalIgnoreCase);

                        if (!IsNonBillSheet(sName) && !isSummaryOrPrelim)
                        {
                            break;
                        }
                    } while (reader.NextResult());

                    var tgtCols = SemanticColumnResolver.ResolveColumns(reader, maxScanRows: 35);

                    if (tgtCols.RateColumn >= 0) mapping.TargetRateColumn = tgtCols.RateColumn;
                    if (tgtCols.DescriptionColumn >= 0) mapping.TargetDescColumn = tgtCols.DescriptionColumn;
                    if (tgtCols.ItemCodeColumn >= 0) mapping.TargetCodeColumn = tgtCols.ItemCodeColumn;
                    if (tgtCols.QuantityColumn >= 0) mapping.TargetQtyColumn = tgtCols.QuantityColumn;
                    if (tgtCols.UnitColumn >= 0) mapping.TargetUnitColumn = tgtCols.UnitColumn;
                }

                mapping.IsAutoDetected = true;
                mapping.SummaryText = $"تم الكشف التلقائي بنجاح: عمود السعر المصدر ({GetColumnLetter(mapping.SourceRateColumn)}) -> عمود السعر المستهدف ({GetColumnLetter(mapping.TargetRateColumn)})";
            }
            catch
            {
                mapping.IsAutoDetected = false;
                mapping.SummaryText = "تم تطبيق التعيين القياسي الافتراضي للمقايسات.";
            }

            return mapping;
        }, ct);
    }

    public static string GetColumnLetter(int colIndex)
    {
        if (colIndex < 0) return "?";
        int div = colIndex;
        string colLetter = string.Empty;
        while (div >= 0)
        {
            colLetter = (char)('A' + (div % 26)) + colLetter;
            div = (div / 26) - 1;
        }
        return colLetter;
    }

    private static bool IsNonBillSheet(string sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName)) return true;
        string trimmed = sheetName.Trim();
        if (NonBillSheetPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (trimmed.Contains("Summary", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Grand", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Contents", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Preamble", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Cover", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("General Notes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("TOC", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("فهرس", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("غلاف", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("ملخص", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static string ExtractBillCode(string sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName) || IsNonBillSheet(sheetName)) 
            return "-";

        // 1. Explicit keyword match: "Bill 01", "Bill 1A", "Bill 06.1A", "Schedule 2", "الباب الأول", "جدول 3"
        var matchKeyword = System.Text.RegularExpressions.Regex.Match(sheetName, @"(?:Bill|Schedule|Package|الباب|جدول)\s*([0-9]+(?:\.[0-9]+)?[a-zA-Z]*|[a-zA-Z][0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (matchKeyword.Success)
        {
            return matchKeyword.Groups[1].Value.ToUpperInvariant();
        }

        // 2. Leading alphanumeric code: e.g. "01 - Civil", "02A Earthworks", "2.1 Prelims"
        var matchLeading = System.Text.RegularExpressions.Regex.Match(sheetName, @"^\s*([0-9]+(?:\.[0-9]+)?[a-zA-Z]?|[a-zA-Z][0-9]+)\b");
        if (matchLeading.Success)
        {
            return matchLeading.Groups[1].Value.ToUpperInvariant();
        }

        // 3. Short prefix before dash if contains digits: "01 - Structure" -> "01"
        int dashIdx = sheetName.IndexOf('-');
        if (dashIdx > 0)
        {
            string prefix = sheetName[..dashIdx].Trim();
            if (prefix.Length <= 6 && prefix.Any(char.IsDigit))
            {
                return prefix.ToUpperInvariant();
            }
        }

        // 4. Arabic ordinal word conversion (e.g. "الباب الأول" -> "1", "الباب الثاني" -> "2")
        if (sheetName.Contains("الأول", StringComparison.OrdinalIgnoreCase) || sheetName.Contains("الاول", StringComparison.OrdinalIgnoreCase)) return "1";
        if (sheetName.Contains("الثاني", StringComparison.OrdinalIgnoreCase) || sheetName.Contains("الثانى", StringComparison.OrdinalIgnoreCase)) return "2";
        if (sheetName.Contains("الثالث", StringComparison.OrdinalIgnoreCase)) return "3";
        if (sheetName.Contains("الرابع", StringComparison.OrdinalIgnoreCase)) return "4";
        if (sheetName.Contains("الخامس", StringComparison.OrdinalIgnoreCase)) return "5";
        if (sheetName.Contains("السادس", StringComparison.OrdinalIgnoreCase)) return "6";
        if (sheetName.Contains("السابع", StringComparison.OrdinalIgnoreCase)) return "7";
        if (sheetName.Contains("الثامن", StringComparison.OrdinalIgnoreCase)) return "8";
        if (sheetName.Contains("التاسع", StringComparison.OrdinalIgnoreCase)) return "9";
        if (sheetName.Contains("العاشر", StringComparison.OrdinalIgnoreCase)) return "10";

        return "-";
    }

    private static string NormalizeName(string name)
    {
        return name.ToLowerInvariant()
            .Replace("bill", "")
            .Replace("schedule", "")
            .Replace("package", "")
            .Replace("trade", "")
            .Replace("no", "")
            .Replace("الباب", "")
            .Replace("باب", "")
            .Replace("جدول", "")
            .Replace("قسم", "")
            .Replace("أعمال", "")
            .Replace(".", "")
            .Replace("-", "")
            .Replace("_", "")
            .Trim();
    }

    private static double ComputeJaccard(string a, string b)
    {
        var tokensA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        var tokensB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        if (tokensA.Count == 0 && tokensB.Count == 0) return 1.0;
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0.0;

        int intersection = 0;
        foreach (var t in tokensA)
        {
            if (tokensB.Contains(t)) intersection++;
        }
        int union = tokensA.Count + tokensB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}
