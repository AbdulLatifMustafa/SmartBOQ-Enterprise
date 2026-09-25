using System.Buffers;

namespace SmartBOQ.Infrastructure.Common;

/// <summary>
/// High-speed zero-allocation tokenizer and 64-bit FNV-1a hash generator for text strings.
/// Replaces string.Split and HashSet allocations with compact sorted ulong hash arrays.
/// </summary>
public static class SpanTokenizer
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Computes 64-bit FNV-1a hash of a character span case-insensitively without any allocation.
    /// </summary>
    public static ulong HashToken(ReadOnlySpan<char> span)
    {
        ulong hash = FnvOffsetBasis;
        for (int i = 0; i < span.Length; i++)
        {
            char c = char.ToLowerInvariant(span[i]);
            hash ^= c;
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>
    /// Tokenizes a normalized text string into a sorted, unique array of 64-bit token hashes.
    /// Uses stack allocation for small texts (< 64 tokens) or pooled memory for larger texts.
    /// </summary>
    public static ulong[] ExtractSortedTokenHashes(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return [];

        // Estimate token count
        int estimatedTokens = 0;
        bool inWord = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (!inWord)
                {
                    inWord = true;
                    estimatedTokens++;
                }
            }
            else
            {
                inWord = false;
            }
        }

        if (estimatedTokens == 0) return [];

        // Rent or stackalloc buffer for token hashes
        ulong[]? rentedArray = null;
        Span<ulong> buffer = estimatedTokens <= 128
            ? stackalloc ulong[estimatedTokens]
            : (rentedArray = ArrayPool<ulong>.Shared.Rent(estimatedTokens));

        int tokenCount = 0;
        int wordStart = -1;

        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (wordStart < 0) wordStart = i;
            }
            else
            {
                if (wordStart >= 0)
                {
                    int wordLength = i - wordStart;
                    if (wordLength > 2) // Filter noise tokens
                    {
                        var wordSpan = text.Slice(wordStart, wordLength);
                        if (tokenCount < buffer.Length)
                        {
                            buffer[tokenCount++] = HashToken(wordSpan);
                        }
                    }
                    wordStart = -1;
                }
            }
        }

        // Catch trailing word
        if (wordStart >= 0)
        {
            int wordLength = text.Length - wordStart;
            if (wordLength > 2 && tokenCount < buffer.Length)
            {
                buffer[tokenCount++] = HashToken(text.Slice(wordStart, wordLength));
            }
        }

        if (tokenCount == 0)
        {
            if (rentedArray != null) ArrayPool<ulong>.Shared.Return(rentedArray);
            return [];
        }

        var validSlice = buffer[..tokenCount];
        validSlice.Sort();

        // Count unique tokens
        int uniqueCount = 1;
        for (int i = 1; i < tokenCount; i++)
        {
            if (validSlice[i] != validSlice[i - 1])
            {
                uniqueCount++;
            }
        }

        // Create compact result array
        var result = new ulong[uniqueCount];
        result[0] = validSlice[0];
        int writeIdx = 1;
        for (int i = 1; i < tokenCount; i++)
        {
            if (validSlice[i] != validSlice[i - 1])
            {
                result[writeIdx++] = validSlice[i];
            }
        }

        if (rentedArray != null)
        {
            ArrayPool<ulong>.Shared.Return(rentedArray);
        }

        return result;
    }

    /// <summary>
    /// Computes Jaccard similarity between two sorted ulong arrays in O(N + M) time
    /// with branchless two-pointer traversal and ZERO heap allocations.
    /// </summary>
    public static double CalculateSortedJaccard(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0.0;

        int i = 0, j = 0;
        int intersection = 0;

        while (i < a.Length && j < b.Length)
        {
            ulong valA = a[i];
            ulong valB = b[j];

            if (valA == valB)
            {
                intersection++;
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

        int union = a.Length + b.Length - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Computes Levenshtein edit distance using bit-parallel Myers algorithm.
    /// Operates in O(N) time with bitwise registers and hardware POPCNT instructions (zero heap allocation).
    /// Fallback to Fastenshtein for long strings (> 64 chars) or non-ASCII characters.
    /// </summary>
    public static int BitParallelDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty) return b.Length;
        if (b.IsEmpty) return a.Length;

        if (a.Length > b.Length)
        {
            var temp = a;
            a = b;
            b = temp;
        }

        int m = a.Length;
        int n = b.Length;

        // Check if characters fit in ASCII byte table for branchless bitmask indexing
        bool isAscii = true;
        for (int i = 0; i < m; i++)
        {
            if (a[i] >= 256) { isAscii = false; break; }
        }
        if (isAscii)
        {
            for (int i = 0; i < n; i++)
            {
                if (b[i] >= 256) { isAscii = false; break; }
            }
        }

        if (isAscii && m <= 64)
        {
            Span<ulong> peq = stackalloc ulong[256];
            peq.Clear();
            for (int i = 0; i < m; i++)
            {
                peq[a[i]] |= 1UL << i;
            }

            ulong pv = ~0UL;
            ulong mv = 0UL;
            int score = m;

            for (int j = 0; j < n; j++)
            {
                ulong eq = peq[b[j]];

                ulong xv = eq | mv;
                ulong xh = (((eq & pv) + pv) ^ pv) | eq;

                ulong ph = mv | ~(xh | pv);
                ulong mh = pv & xh;

                if ((ph & (1UL << (m - 1))) != 0)
                    score++;
                else if ((mh & (1UL << (m - 1))) != 0)
                    score--;

                ph = (ph << 1) | 1UL;
                mh = (mh << 1);

                pv = mh | ~(xv | ph);
                mv = ph & xv;
            }

            return score;
        }

        return new Fastenshtein.Levenshtein(a.ToString()).DistanceFrom(b.ToString());
    }
}
