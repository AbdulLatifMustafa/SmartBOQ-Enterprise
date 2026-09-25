using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;

namespace SmartBOQ.Infrastructure.Matching;

/// <summary>
/// Abstract base class defining tokenization, set similarity, and structural comparison utilities
/// for Bill of Quantities item matchers.
/// </summary>
public abstract class BaseItemMatcher : IItemMatcher
{
    public abstract Task<IReadOnlyList<BoqMatchedPair>> MatchItemsAsync(
        IReadOnlyList<BoqItem> targetItems,
        IReadOnlyList<BoqItem> sourceItems,
        double sensitivity = 0.85,
        CancellationToken ct = default);

    /// <summary>
    /// Extracts sorted 64-bit token hashes using high-speed zero-allocation span parsing.
    /// </summary>
    protected static ulong[] ExtractTokenHashes(ReadOnlySpan<char> text)
    {
        return SpanTokenizer.ExtractSortedTokenHashes(text);
    }

    /// <summary>
    /// Computes Jaccard similarity between two sorted ulong token hash arrays in O(N + M) with zero allocations.
    /// </summary>
    protected static double CalculateSortedHashJaccard(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        return SpanTokenizer.CalculateSortedJaccard(a, b);
    }

    /// <summary>
    /// Computes the trigonometric cosine angle cos(theta) between two token bags:
    /// cos(theta) = |A ∩ B| / (sqrt(|A|) * sqrt(|B|))
    /// </summary>
    protected static double CalculateTrigonometricCosine(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0.0;

        int i = 0, j = 0;
        int intersection = 0;

        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                intersection++;
                i++;
                j++;
            }
            else if (a[i] < b[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        if (intersection == 0) return 0.0;
        double denominator = Math.Sqrt(a.Length) * Math.Sqrt(b.Length);
        return denominator > 0.0 ? Math.Clamp(intersection / denominator, 0.0, 1.0) : 0.0;
    }

    /// <summary>
    /// Tokenizes a text block into a distinct set of significant keywords (length > 2).
    /// </summary>
    protected static HashSet<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p.Length > 2)
            {
                tokens.Add(p);
            }
        }
        return tokens;
    }

    /// <summary>
    /// Computes Jaccard similarity coefficient between two word token sets.
    /// </summary>
    protected static double CalculateJaccard(HashSet<string> setA, HashSet<string> setB)
    {
        if (setA.Count == 0 || setB.Count == 0) return 0.0;
        int intersection = 0;
        foreach (var item in setA)
        {
            if (setB.Contains(item))
            {
                intersection++;
            }
        }
        int union = setA.Count + setB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Checks whether two bill identifiers share the same structural root prefix.
    /// </summary>
    protected static bool AreBillsRelated(string billA, string billB)
    {
        if (string.IsNullOrWhiteSpace(billA) || string.IsNullOrWhiteSpace(billB)) return false;
        int lenA = Math.Min(billA.Length, 7);
        int lenB = Math.Min(billB.Length, 7);
        return billA[..lenA].Equals(billB[..lenB], StringComparison.OrdinalIgnoreCase);
    }
}
