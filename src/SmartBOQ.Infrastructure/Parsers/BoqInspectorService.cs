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

    public async Task<BoqFileInfo> InspectWorkbookAsync(string filePath, BoqFileRole? preferredRole = null, CancellationToken ct = default)
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
            int totalPricedRows = 0;
            int totalSampledDataRows = 0;
            bool hasTenderMetadata = false;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                ct.ThrowIfCancellationRequested();
                string sheetName = string.IsNullOrWhiteSpace(reader.Name) ? "Sheet1" : reader.Name.Trim();
                if (reader.ResultsCount > 1 && IsNonBillSheet(sheetName))
                {
                    sheetSummaries.Add(new BoqSheetSummary
                    {
                        SheetName = sheetName,
                        BillCode = ExtractBillCode(sheetName),
                        EstimatedRows = 0,
                        IsProvisionalSum = false,
                        IsNonBillSheet = true,
                        DetectedCurrency = detectedCurrency,
                        SourceFileName = fileInfo.Name
                    });
                    continue;
                }

                bool isPsSheet = sheetName.Equals("Bill 6 Provisional Sum", StringComparison.OrdinalIgnoreCase) ||
                                 sheetName.StartsWith("Bill 6 Provisional", StringComparison.OrdinalIgnoreCase) ||
                                 sheetName.Contains("Provisional Sum", StringComparison.OrdinalIgnoreCase) ||
                                 sheetName.Contains("مبلغ احتياطي", StringComparison.OrdinalIgnoreCase) ||
                                 sheetName.Contains("مبالغ احتياطية", StringComparison.OrdinalIgnoreCase);

                // Buffer the first 40 rows of this sheet
                var buffer = new List<string[]>(40);
                var rawBuffer = new List<object?[]>(40);
                int rowsBuffered = 0;

                while (rowsBuffered < 40 && reader.Read())
                {
                    rowsBuffered++;
                    int fieldCount = reader.FieldCount;
                    var strVals = new string[fieldCount];
                    var rawVals = new object?[fieldCount];
                    for (int c = 0; c < fieldCount; c++)
                    {
                        rawVals[c] = reader.GetValue(c);
                        strVals[c] = rawVals[c]?.ToString()?.Trim() ?? string.Empty;
                    }
                    buffer.Add(strVals);
                    rawBuffer.Add(rawVals);
                }

                if (buffer.Count == 0) continue;

                if (SemanticColumnResolver.IsPivotOrSummarySheet(buffer))
                {
                    sheetSummaries.Add(new BoqSheetSummary
                    {
                        SheetName = sheetName,
                        BillCode = ExtractBillCode(sheetName),
                        EstimatedRows = 0,
                        IsProvisionalSum = false,
                        IsNonBillSheet = true,
                        DetectedCurrency = detectedCurrency,
                        SourceFileName = fileInfo.Name
                    });
                    continue;
                }

                var resolvedCols = SemanticColumnResolver.ResolveColumnsFromRows(buffer, buffer[0].Length);
                if (resolvedCols.HasDetectedHeaders && resolvedCols.HeaderRowIndex > 0 && resolvedCols.HeaderRowIndex <= buffer.Count)
                {
                    var hRow = buffer[resolvedCols.HeaderRowIndex - 1];
                    for (int c = 0; c < hRow.Length; c++)
                    {
                        string h = hRow[c];
                        if (!string.IsNullOrWhiteSpace(h) && h.Length <= 40)
                        {
                            columnHeaders.Add(h);
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(resolvedCols.DetectedCurrency) && resolvedCols.DetectedCurrency != "EGP")
                {
                    detectedCurrency = resolvedCols.DetectedCurrency;
                }

                // Check pre-header rows for tender metadata (Employer, Tender No, Project, Engineer, Consultant)
                int headerZeroIdx = Math.Max(0, resolvedCols.HeaderRowIndex - 1);
                for (int r = 0; r < Math.Min(headerZeroIdx, buffer.Count); r++)
                {
                    for (int c = 0; c < buffer[r].Length; c++)
                    {
                        string val = buffer[r][c];
                        if (string.IsNullOrWhiteSpace(val)) continue;
                        if (val.Contains("Employer", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("Tender No", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("Tender Title", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("Project", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("Engineer", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("Consultant", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("Owner", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("المالك", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("الاستشاري", StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("المناقصة", StringComparison.OrdinalIgnoreCase))
                        {
                            hasTenderMetadata = true;
                            break;
                        }
                    }
                    if (hasTenderMetadata) break;
                }

                // Inspect buffered data rows for priced rates
                int rateCol = resolvedCols.RateColumn;
                int amtCol = resolvedCols.TotalAmountColumn;
                int descCol = resolvedCols.DescriptionColumn;
                int qtyCol = resolvedCols.QuantityColumn;

                for (int r = resolvedCols.HeaderRowIndex; r < buffer.Count; r++)
                {
                    string desc = descCol >= 0 && descCol < buffer[r].Length ? buffer[r][descCol] : string.Empty;
                    string qtyStr = qtyCol >= 0 && qtyCol < buffer[r].Length ? buffer[r][qtyCol] : string.Empty;

                    // Skip metadata or empty rows
                    if (string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(qtyStr)) continue;

                    totalSampledDataRows++;

                    if (rateCol >= 0 && rateCol < rawBuffer[r].Length)
                    {
                        var rawVal = rawBuffer[r][rateCol];
                        if (TryParsePositiveDecimal(rawVal, out decimal rate) && rate > 0.0001m)
                        {
                            totalPricedRows++;
                        }
                    }
                    else if (amtCol >= 0 && amtCol < rawBuffer[r].Length)
                    {
                        var rawVal = rawBuffer[r][amtCol];
                        if (TryParsePositiveDecimal(rawVal, out decimal amt) && amt > 0.0001m)
                        {
                            totalPricedRows++;
                        }
                    }
                }

                // Read remaining rows of this sheet for total estimated count
                int remainingRows = 0;
                while (reader.Read())
                {
                    remainingRows++;
                }
                int totalSheetRows = buffer.Count + remainingRows;
                int estimatedDataRows = Math.Max(0, totalSheetRows - resolvedCols.HeaderRowIndex);
                totalEstimatedItems += estimatedDataRows;

                sheetSummaries.Add(new BoqSheetSummary
                {
                    SheetName = sheetName,
                    BillCode = ExtractBillCode(sheetName),
                    EstimatedRows = estimatedDataRows,
                    StartRowIndex = resolvedCols.HeaderRowIndex + 1,
                    IsProvisionalSum = isPsSheet,
                    IsNonBillSheet = false,
                    DetectedCurrency = detectedCurrency,
                    SourceFileName = fileInfo.Name
                });

            } while (reader.NextResult());

            bool hasPricedRates = totalPricedRows > 0;
            var detectedRole = InferRole(fileInfo.Name, hasPricedRates, totalPricedRows, totalSampledDataRows, hasTenderMetadata, sheetSummaries.Count);
            var finalRole = preferredRole ?? detectedRole;

            return new BoqFileInfo
            {
                FilePath = filePath,
                FileName = fileInfo.Name,
                FileSizeBytes = fileInfo.Length,
                Role = finalRole,
                DetectedRole = detectedRole,
                HasPricedRates = hasPricedRates,
                PricedItemsCount = totalPricedRows,
                SampledItemsCount = totalSampledDataRows,
                HasTenderMetadata = hasTenderMetadata,
                SheetsCount = sheetSummaries.Count,
                TotalEstimatedItems = totalEstimatedItems,
                DetectedCurrency = detectedCurrency,
                Sheets = sheetSummaries,
                AvailableColumns = columnHeaders.OrderBy(c => c).ToList(),
                IsPrimary = finalRole == BoqFileRole.ConsultantTarget || finalRole == BoqFileRole.ContractorPriced
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

            // Default column fallback mappings
            // Contractor flat: Rate=17, Desc=11, Code=10, Qty=14, Unit=13
            // Consultant hierarchical: Rate=6, Desc=2, Code=0, Qty=4, Unit=5

            try
            {
                if (File.Exists(sourceFilePath))
                {
                    using var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32768, FileOptions.SequentialScan);
                    using var reader = ExcelReaderFactory.CreateReader(stream);

                    // Advance to first work bill sheet with detailed line items
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

                    // Advance to first work bill sheet with detailed line items
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
            trimmed.Contains("Notes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Note", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("ملاحظات", StringComparison.OrdinalIgnoreCase) ||
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

        // 1. Explicit keyword match: "Bill 01", "Bill 1A", "Bill 06.1A", "Schedule 2", or Arabic bill keywords
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

        // 4. Arabic ordinal word conversion (e.g. First -> "1", Second -> "2")
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

    public static BoqFileRole InferRole(
        string fileName,
        bool hasPricedRates,
        int pricedItemsCount,
        int sampledItemsCount,
        bool hasTenderMetadata,
        int sheetsCount)
    {
        // 1. Content Ground Truth: If workbook has 0 priced rows, it CANNOT be a pricing source.
        // It is strictly an unpriced Consultant Target schedule waiting for rates.
        if (!hasPricedRates || pricedItemsCount == 0)
        {
            return BoqFileRole.ConsultantTarget;
        }

        string name = fileName.ToLowerInvariant();

        // 2. High-priority Contractor / Pricing signatures:
        // Keywords like Candy, CCS, Priced, quotation, or Arabic pricing terms indicate pricing data source.
        bool hasExplicitContractorKeyword = name.Contains("candy") ||
                                            name.Contains("ccs") ||
                                            name.Contains("priced") ||
                                            name.Contains("pricing") ||
                                            name.Contains("selling") ||
                                            name.Contains("vendor") ||
                                            name.Contains("supplier") ||
                                            name.Contains("subcon") ||
                                            name.Contains("quotation") ||
                                            name.Contains("offer") ||
                                            name.Contains("عرض سعر") ||
                                            name.Contains("تسعير") ||
                                            name.Contains("مسعر") ||
                                            name.Contains("مورد") ||
                                            name.Contains("مقاول");

        if (hasExplicitContractorKeyword && !name.Contains("unpriced") && !name.Contains("غير مسعر"))
        {
            return BoqFileRole.ContractorPriced;
        }

        // 3. Explicit Consultant/Tender keywords in filename or metadata
        bool hasConsultantKeyword = name.Contains("consultant") ||
                                    name.Contains("tender") ||
                                    name.Contains("unpriced") ||
                                    name.Contains("client") ||
                                    name.Contains("owner") ||
                                    name.Contains("employer") ||
                                    name.Contains("engineer") ||
                                    name.Contains("طرح") ||
                                    name.Contains("استشاري") ||
                                    name.Contains("مناقصة") ||
                                    name.Contains("غير مسعر") ||
                                    name.Contains("مالك") ||
                                    name.Contains("عميل");

        if (hasConsultantKeyword || hasTenderMetadata)
        {
            // Tender booklet format or consultant name - even if it contains rates (e.g. Rev_02 budget),
            // it acts as the primary consultant structure target.
            return BoqFileRole.ConsultantTarget;
        }

        // Default for files containing valid positive rates without consultant tokens: Contractor Priced source
        return BoqFileRole.ContractorPriced;
    }

    private static bool TryParsePositiveDecimal(object? val, out decimal result)
    {
        result = 0m;
        if (val == null) return false;

        if (val is double d)
        {
            if (d > 0.00001 && !double.IsInfinity(d) && !double.IsNaN(d))
            {
                result = (decimal)d;
                return true;
            }
            return false;
        }
        if (val is decimal dec)
        {
            if (dec > 0.00001m)
            {
                result = dec;
                return true;
            }
            return false;
        }
        if (val is float f)
        {
            if (f > 0.00001f && !float.IsInfinity(f) && !float.IsNaN(f))
            {
                result = (decimal)f;
                return true;
            }
            return false;
        }
        if (val is int i)
        {
            if (i > 0)
            {
                result = i;
                return true;
            }
            return false;
        }
        if (val is long l)
        {
            if (l > 0)
            {
                result = l;
                return true;
            }
            return false;
        }

        string str = val.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(str)) return false;

        str = str.Replace(",", "").Replace("$", "").Replace("£", "").Replace("€", "").Replace("EGP", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (decimal.TryParse(str, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0.00001m)
        {
            result = parsed;
            return true;
        }

        return false;
    }
}
