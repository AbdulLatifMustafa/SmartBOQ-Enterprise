using System.Buffers;
using System.Globalization;
using System.Text;
using ExcelDataReader;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;

namespace SmartBOQ.Infrastructure.Parsers;

/// <summary>
/// Abstract base class providing common streaming, parsing, and normalization infrastructure
/// for all Excel Bill of Quantities readers.
/// </summary>
public abstract class BaseBoqReader : IBoqReader
{
    static BaseBoqReader()
    {
        // Register code pages provider required by ExcelDataReader for legacy and modern encodings
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public virtual Task<IReadOnlyList<BoqItem>> ReadContractorFlatBoqAsync(string filePath, CancellationToken ct = default)
    {
        return ReadContractorFlatBoqAsync(filePath, null, ct);
    }

    public virtual Task<IReadOnlyList<BoqItem>> ReadContractorFlatBoqAsync(string filePath, ColumnMappingModel? columnMappings, CancellationToken ct = default)
    {
        throw new NotSupportedException($"{GetType().Name} does not support reading contractor flat BOQ files.");
    }

    public virtual Task<IReadOnlyList<BoqSheet>> ReadConsultantHierarchicalBoqAsync(string filePath, CancellationToken ct = default)
    {
        throw new NotSupportedException($"{GetType().Name} does not support reading consultant hierarchical BOQ files.");
    }

    private static readonly string[] SharedNonBillPrefixes =
    [
        "TABLE OF CONTENTS", "PREAMBLE", "SCHEDULE OF INSURANCE", "INSTRUCTION",
        "GRAND SUMMARY", "COVER", "DAYWORKS", "PRICE ANALYSIS", "AUDIT", "DASHBOARD",
        "EXECUTIVE", "PRICING_LINKAGE", "LINKAGE", "فهرس", "غلاف", "ملخص"
    ];

    /// <summary>
    /// Checks whether an Excel worksheet is an administrative/summary sheet rather than a pricing bill.
    /// </summary>
    protected static bool IsNonBillSheet(string sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName)) return true;
        string trimmed = sheetName.Trim();
        foreach (var p in SharedNonBillPrefixes)
        {
            if (trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return trimmed.Contains("Summary", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("Preamble", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("ملخص", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Safely retrieves an object value from an open Excel reader without out-of-bounds exceptions.
    /// </summary>
    protected static object? GetSafeValue(IExcelDataReader reader, int index)
    {
        return index < reader.FieldCount ? reader.GetValue(index) : null;
    }

    /// <summary>
    /// Safely retrieves a trimmed canonical string value from an open Excel reader via string pool.
    /// </summary>
    protected static string GetSafeString(IExcelDataReader reader, int index)
    {
        if (index >= reader.FieldCount) return string.Empty;
        var val = reader.GetValue(index);
        if (val == null) return string.Empty;
        return CompactStringPool.Shared.GetOrAdd(val.ToString()?.Trim());
    }

    /// <summary>
    /// Parses any numeric or string object into a high-precision decimal.
    /// </summary>
    protected static decimal ParseDecimal(object? val)
    {
        if (val == null) return 0m;
        if (val is double d) return Convert.ToDecimal(d);
        if (val is decimal dec) return dec;
        if (val is int i) return i;
        if (val is long l) return l;

        string s = val.ToString()?.Trim() ?? string.Empty;
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }
        return 0m;
    }

    /// <summary>
    /// Normalizes civil engineering units of measurement into unified canonical tokens.
    /// </summary>
    protected static string NormalizeUnit(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return string.Empty;
        string u = unit.Trim().ToLowerInvariant();
        string normalized = u switch
        {
            "m2" or "sqm" or "sq.m" or "m²" or "م2" or "م²" or "متر مربع" or "متر2" => "m2",
            "m3" or "cum" or "cu.m" or "m³" or "م3" or "م³" or "متر مكعب" or "متر3" => "m3",
            "lm" or "m" or "lin.m" or "mtr" or "م.ط" or "متر طولي" or "متر" => "m",
            "nr" or "no" or "nos" or "item" or "عدد" or "بند" or "حبة" or "قطعة" => "item",
            "ton" or "tonne" or "tons" or "طن" => "ton",
            "kg" or "kgs" or "كجم" or "كيلو" or "كيلوجرام" => "kg",
            "ls" or "sum" or "مقطوعية" or "جملة" or "مقطوع" => "sum",
            _ => u
        };
        return CompactStringPool.Shared.GetOrAdd(normalized);
    }

    /// <summary>
    /// Strips punctuation and excessive whitespace using stackalloc/ArrayPool without StringBuilder heap churn.
    /// </summary>
    protected static string NormalizeDescription(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        int len = text.Length;
        char[]? rented = null;
        Span<char> buffer = len <= 256 ? stackalloc char[len] : (rented = ArrayPool<char>.Shared.Rent(len));

        int outIdx = 0;
        bool lastWasSpace = true; // Prevents leading spaces

        for (int i = 0; i < len; i++)
        {
            char c = text[i];

            // Arabic Diacritics / Tatweel stripping
            if (c >= '\u064B' && c <= '\u065F') continue; // Fatha, Damma, Kasra, Shadda, Sukun, Tanween
            if (c == '\u0640') continue; // Tatweel / Kashida
            if (c == '\u0670') continue; // Superscript Alef

            // Arabic Letter Normalization
            if (c == 'أ' || c == 'إ' || c == 'آ' || c == 'ٱ') c = 'ا';
            else if (c == 'ة') c = 'ه';
            else if (c == 'ى') c = 'ي';

            if (char.IsLetterOrDigit(c))
            {
                buffer[outIdx++] = char.ToLowerInvariant(c);
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    buffer[outIdx++] = ' ';
                    lastWasSpace = true;
                }
            }
        }

        // Trim trailing space
        if (outIdx > 0 && buffer[outIdx - 1] == ' ')
        {
            outIdx--;
        }

        string result = CompactStringPool.Shared.GetOrAdd(buffer[..outIdx]);

        if (rented != null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }

        return result;
    }
}
