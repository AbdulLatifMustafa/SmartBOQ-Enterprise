using System.Collections.Concurrent;

namespace SmartBOQ.Infrastructure.Common;

/// <summary>
/// High-throughput, lock-free canonical string interning pool for Bill of Quantities items.
/// Drastically eliminates redundant string allocations across thousands of repetitive cells
/// (e.g. units 'm2', 'm3', currencies 'EGP', 'USD', bill codes, and common keywords).
/// </summary>
public sealed class CompactStringPool
{
    public static readonly CompactStringPool Shared = new();

    private readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);

    public CompactStringPool()
    {
        // Pre-seed common civil engineering tokens to prevent initial contention
        string[] commonTokens =
        [
            "EGP", "USD", "EUR", "GBP",
            "m2", "m3", "m", "item", "ton", "kg", "nr", "sum", "ls", "lin.m",
            "Rate only", "Provisional Sum", "Variation Order",
            "Bill", "Section", "Preliminaries", "Earthworks", "Concrete", "Finishes"
        ];

        foreach (var token in commonTokens)
        {
            _pool.TryAdd(token, token);
        }
    }

    /// <summary>
    /// Returns the canonical shared string instance for the given string.
    /// </summary>
    public string GetOrAdd(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length > 256) return value; // Don't pool excessively long paragraphs
        return _pool.GetOrAdd(value, value);
    }

    /// <summary>
    /// Returns the canonical shared string instance for the character span without allocating on cache hits.
    /// Uses .NET 9+ / .NET 10 AlternateLookup to achieve true zero-allocation lookups on ReadOnlySpan.
    /// </summary>
    public string GetOrAdd(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return string.Empty;
        if (span.Length > 256) return span.ToString();

        var lookup = _pool.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(span, out string? existing))
        {
            return existing;
        }

        string created = span.ToString();
        lookup.TryAdd(span, created);
        return created;
    }

    public int Count => _pool.Count;

    public void Clear() => _pool.Clear();
}
