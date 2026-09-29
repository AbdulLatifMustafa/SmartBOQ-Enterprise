using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;

/// <summary>
/// High-performance, zero-allocation 64-bit hashed token set representation.
/// Enables ultra-fast SIMD bitwise set operations and token intersection without heap allocations.
/// </summary>
public readonly struct CognitiveTokenSet
{
    private static readonly SearchValues<char> WordDelimiters = SearchValues.Create(" \t\r\n,.;:()[]{}-_/\\|*+~`'\"؟،=><");

    public ulong[] Hashes { get; }
    public int Count => Hashes.Length;
    public ulong BloomFilter { get; }

    public CognitiveTokenSet(ulong[] sortedHashes, ulong bloomFilter)
    {
        Hashes = sortedHashes;
        BloomFilter = bloomFilter;
    }

    /// <summary>
    /// Parses text into a sorted deduplicated array of 64-bit token hashes with a 64-bit Bloom filter.
    /// Operates with zero heap string allocations during tokenization.
    /// </summary>
    public static CognitiveTokenSet FromText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return new CognitiveTokenSet(Array.Empty<ulong>(), 0UL);
        }

        // Temporary stack allocation for token hashes up to 128 words
        Span<ulong> stackHashes = stackalloc ulong[Math.Min(128, text.Length / 2 + 1)];
        int hashCount = 0;
        ulong bloom = 0UL;

        int start = 0;
        while (start < text.Length)
        {
            // Skip delimiters using SIMD SearchValues
            var remaining = text[start..];
            int nonDelimOffset = 0;
            while (nonDelimOffset < remaining.Length && WordDelimiters.Contains(remaining[nonDelimOffset]))
            {
                nonDelimOffset++;
            }

            start += nonDelimOffset;
            if (start >= text.Length) break;

            // Find word end
            remaining = text[start..];
            int delimOffset = remaining.IndexOfAny(WordDelimiters);
            int wordLen = delimOffset >= 0 ? delimOffset : remaining.Length;

            var word = remaining[..wordLen];
            AddToken(ref stackHashes, ref hashCount, ref bloom, word);

            start += wordLen;
        }

        if (hashCount == 0)
        {
            return new CognitiveTokenSet(Array.Empty<ulong>(), 0UL);
        }

        // Deduplicate and sort
        var slice = stackHashes[..hashCount];
        slice.Sort();

        // Count unique
        int uniqueCount = 1;
        for (int i = 1; i < slice.Length; i++)
        {
            if (slice[i] != slice[i - 1]) uniqueCount++;
        }

        var result = new ulong[uniqueCount];
        result[0] = slice[0];
        int writeIdx = 1;
        for (int i = 1; i < slice.Length; i++)
        {
            if (slice[i] != slice[i - 1])
            {
                result[writeIdx++] = slice[i];
            }
        }

        return new CognitiveTokenSet(result, bloom);
    }

    /// <summary>
    /// Computes intersection count between two token sets with Bloom filter early-rejection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IntersectCount(in CognitiveTokenSet other)
    {
        // Early rejection via Bloom filter bitwise AND
        if ((BloomFilter & other.BloomFilter) == 0UL)
        {
            return 0;
        }

        var a = Hashes;
        var b = other.Hashes;
        int i = 0, j = 0, matches = 0;

        while (i < a.Length && j < b.Length)
        {
            ulong valA = a[i];
            ulong valB = b[j];

            if (valA == valB)
            {
                matches++;
                i++;
                j++;
            }
            else if (valA < valB)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return matches;
    }

    /// <summary>
    /// Calculates token coverage: |A ∩ B| / min(|A|, |B|).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double CalculateTokenCoverage(in CognitiveTokenSet other)
    {
        if (Count == 0 || other.Count == 0) return 0.0;
        int common = IntersectCount(other);
        return (double)common / Math.Min(Count, other.Count);
    }

    /// <summary>
    /// Calculates Jaccard similarity: |A ∩ B| / |A ∪ B|.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double CalculateJaccard(in CognitiveTokenSet other)
    {
        if (Count == 0 || other.Count == 0) return 0.0;
        int common = IntersectCount(other);
        int union = Count + other.Count - common;
        return union > 0 ? (double)common / union : 0.0;
    }

    /// <summary>
    /// 64-bit case-insensitive FNV-1a hash of a character span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ComputeHash64(ReadOnlySpan<char> span)
    {
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        ulong hash = FnvOffsetBasis;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];

            // Skip Arabic tatweel and diacritics / tashkeel
            if (c == 'ـ' || (c >= '\u064B' && c <= '\u065F')) continue;

            // Arabic letter normalization: unify Alef forms, Taa Marbuta, and Yaa
            if (c is 'أ' or 'إ' or 'آ') c = 'ا';
            else if (c is 'ة') c = 'ه';
            else if (c is 'ى') c = 'ي';
            else c = char.ToLowerInvariant(c);

            hash ^= c;
            hash *= FnvPrime;
        }
        return hash;
    }

    private static void AddToken(ref Span<ulong> stackHashes, ref int hashCount, ref ulong bloom, ReadOnlySpan<char> rawWord)
    {
        if (rawWord.Length < 2) return;

        // Check for digit-letter boundary (e.g. 16mm or 400W)
        int splitIdx = -1;
        for (int i = 0; i < rawWord.Length - 1; i++)
        {
            if (char.IsDigit(rawWord[i]) && char.IsLetter(rawWord[i + 1]))
            {
                splitIdx = i + 1;
                break;
            }
        }

        if (splitIdx > 0)
        {
            AddSingleToken(ref stackHashes, ref hashCount, ref bloom, rawWord[..splitIdx]);
            AddSingleToken(ref stackHashes, ref hashCount, ref bloom, rawWord[splitIdx..]);
        }
        else
        {
            AddSingleToken(ref stackHashes, ref hashCount, ref bloom, rawWord);
        }
    }

    private static void AddSingleToken(ref Span<ulong> stackHashes, ref int hashCount, ref ulong bloom, ReadOnlySpan<char> token)
    {
        if (token.Length < 2) return;
        var stemmed = StemWord(token);
        ulong h = ComputeHash64(stemmed);
        if (hashCount < stackHashes.Length)
        {
            stackHashes[hashCount++] = h;
            bloom |= (1UL << (int)(h % 64UL));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> StemWord(ReadOnlySpan<char> word)
    {
        // Arabic article and preposition prefix stripping
        if (word.Length >= 4)
        {
            if (word.StartsWith("ال"))
            {
                word = word[2..];
            }
            else if (word.StartsWith("وال") && word.Length >= 5)
            {
                word = word[3..];
            }
            else if (word.StartsWith("بال") && word.Length >= 5)
            {
                word = word[3..];
            }
            else if (word.StartsWith("لل") && word.Length >= 5)
            {
                word = word[2..];
            }
        }

        if (word.Length > 5 && word.EndsWith("ing", StringComparison.OrdinalIgnoreCase))
        {
            return word[..^3];
        }
        if (word.Length > 6 && word.EndsWith("ation", StringComparison.OrdinalIgnoreCase))
        {
            return word[..^5];
        }
        if (word.Length > 5 && word.EndsWith("ed", StringComparison.OrdinalIgnoreCase))
        {
            return word[..^2];
        }
        if (word.Length > 3 && (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase)))
        {
            return word[..^1];
        }
        return word;
    }
}
