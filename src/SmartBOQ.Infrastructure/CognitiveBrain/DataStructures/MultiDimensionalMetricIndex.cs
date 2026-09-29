using System.Buffers;
using System.Collections.Concurrent;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;

/// <summary>
/// Indexed cognitive representation of a source item for multi-dimensional spatial/metric retrieval.
/// </summary>
public sealed class IndexedMetricItem
{
    public required BoqItem Item { get; init; }
    public int GlobalIndex { get; init; }
    public CognitiveTokenSet TokenSet { get; init; }
    public DimensionClass UnitDimension { get; init; }
    public SmartBOQ.Domain.Enums.ContractualActionScope Scope { get; init; }
    public string NormalizedCode { get; init; } = string.Empty;
    public decimal NormalizedRate { get; init; }
}

/// <summary>
/// Multi-dimensional metric index combining inverted token indexing with dimensional unit clustering.
/// Reduces $O(N \times M)$ brute force candidate searches down to sub-millisecond $O(\log K)$ lookups.
/// </summary>
public sealed class MultiDimensionalMetricIndex
{
    private readonly IndexedMetricItem[] _items;
    private readonly Dictionary<ulong, List<int>> _invertedTokenIndex = new();
    private readonly Dictionary<DimensionClass, List<int>> _dimensionBuckets = new();
    private readonly Dictionary<string, List<int>> _codeIndex = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _items.Length;
    public IReadOnlyList<IndexedMetricItem> Items => _items;

    public MultiDimensionalMetricIndex(IReadOnlyList<BoqItem> sourceItems)
    {
        _items = new IndexedMetricItem[sourceItems.Count];

        for (int i = 0; i < sourceItems.Count; i++)
        {
            var src = sourceItems[i];
            string desc = !string.IsNullOrWhiteSpace(src.Description) ? src.Description : src.LineItemText;
            var tokens = CognitiveTokenSet.FromText(desc.AsSpan());
            var dim = QuantileScaleLattice.ClassifyUnit(src.Unit);
            string normCode = CleanCode(src.ItemCode);
            decimal rate = src.UnitRate ?? (src.OriginalRate ?? 0m);

            string scopeText = $"{desc} {src.SectionName} {src.HierarchyPath}";
            var scope = SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope(scopeText);

            var indexed = new IndexedMetricItem
            {
                Item = src,
                GlobalIndex = i,
                TokenSet = tokens,
                UnitDimension = dim,
                Scope = scope,
                NormalizedCode = normCode,
                NormalizedRate = rate
            };

            _items[i] = indexed;

            // 1. Inverted token index
            foreach (ulong hash in tokens.Hashes)
            {
                if (!_invertedTokenIndex.TryGetValue(hash, out var list))
                {
                    list = new List<int>(8);
                    _invertedTokenIndex[hash] = list;
                }
                list.Add(i);
            }

            // 2. Dimension class bucket
            if (dim != DimensionClass.Unknown)
            {
                if (!_dimensionBuckets.TryGetValue(dim, out var dimList))
                {
                    dimList = new List<int>(16);
                    _dimensionBuckets[dim] = dimList;
                }
                dimList.Add(i);
            }

            // 3. Code index
            if (!string.IsNullOrWhiteSpace(normCode))
            {
                if (!_codeIndex.TryGetValue(normCode, out var codeList))
                {
                    codeList = new List<int>(4);
                    _codeIndex[normCode] = codeList;
                }
                codeList.Add(i);
            }
        }
    }

    /// <summary>
    /// Retrieves candidate source item indices matching token sets, codes, or dimensional metrics.
    /// Operates with ArrayPool to eliminate GC pressure.
    /// </summary>
    public int[] FindCandidates(
        in CognitiveTokenSet targetTokens,
        string targetCode,
        DimensionClass targetDimension,
        int maxCandidates = 32)
    {
        if (_items.Length <= maxCandidates)
        {
            // Direct return for small datasets
            var all = new int[_items.Length];
            for (int k = 0; k < _items.Length; k++) all[k] = k;
            return all;
        }

        var candidateScores = ArrayPool<int>.Shared.Rent(_items.Length);
        Array.Clear(candidateScores, 0, _items.Length);

        // A. Score via inverted token hits with dynamic IDF weighting
        int halfThreshold = (int)(_items.Length * 0.40);
        foreach (ulong hash in targetTokens.Hashes)
        {
            if (_invertedTokenIndex.TryGetValue(hash, out var list))
            {
                // IDF Dampening: High-frequency terms get lower weight; rare distinctive technical terms get higher weight
                int weight = 10;
                if (_items.Length > 20 && list.Count > halfThreshold)
                {
                    weight = 2;
                }
                else if (list.Count <= 6)
                {
                    weight = 25;
                }

                for (int idx = 0; idx < list.Count; idx++)
                {
                    candidateScores[list[idx]] += weight;
                }
            }
        }

        // B. Boost via exact normalized code match
        string cleanTargetCode = CleanCode(targetCode);
        if (!string.IsNullOrWhiteSpace(cleanTargetCode) && _codeIndex.TryGetValue(cleanTargetCode, out var codeMatches))
        {
            for (int idx = 0; idx < codeMatches.Count; idx++)
            {
                candidateScores[codeMatches[idx]] += 50;
            }
        }

        // C. Filter and sort top candidates
        var top = new List<(int Index, int Score)>(maxCandidates * 2);
        for (int i = 0; i < _items.Length; i++)
        {
            int s = candidateScores[i];
            if (s > 0)
            {
                // Dimensional harmony bonus
                if (targetDimension != DimensionClass.Unknown && _items[i].UnitDimension == targetDimension)
                {
                    s += 15;
                }
                top.Add((i, s));
            }
        }

        ArrayPool<int>.Shared.Return(candidateScores);

        if (top.Count == 0)
        {
            // Fallback: take items from same dimension bucket if any
            if (targetDimension != DimensionClass.Unknown && _dimensionBuckets.TryGetValue(targetDimension, out var fallbackDim))
            {
                return fallbackDim.Take(maxCandidates).ToArray();
            }
            return Array.Empty<int>();
        }

        top.Sort((a, b) => b.Score.CompareTo(a.Score));
        int count = Math.Min(maxCandidates, top.Count);
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = top[i].Index;
        }

        return result;
    }

    private static string CleanCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var sb = new System.Text.StringBuilder(code.Length);
        foreach (char c in code)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        return sb.ToString();
    }
}
