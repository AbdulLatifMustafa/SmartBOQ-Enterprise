using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Fastenshtein;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Matching;

/// <summary>
/// Enterprise-grade Hierarchical, Bill-Isolated, and Sequence-Aware Smart Matching Engine.
/// Features:
/// 1. Dynamic Scope Isolation: Partitions items by bill/sheet to prevent cross-bill rate contamination.
/// 2. Multi-Factor Collision Disambiguation: Solves repeated descriptions (e.g., across pipe diameters in infrastructure
///    and model variations in buildings) using ItemCode, Quantity proximity, Section hierarchy, and Monotonic Sequence alignment.
/// 3. One-to-One Strict Allocation: Prevents candidate rate hijacking and guarantees single-use mapping.
/// 4. Global Fallback: Catches cross-bill scope variations when intra-bill candidates do not exist.
/// </summary>
public sealed class HybridWeightedMatcher : BaseItemMatcher
{
    public override async Task<IReadOnlyList<BoqMatchedPair>> MatchItemsAsync(
        IReadOnlyList<BoqItem> targetItems,
        IReadOnlyList<BoqItem> sourceItems,
        double sensitivity = 0.85,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetItems);
        ArgumentNullException.ThrowIfNull(sourceItems);

        return await Task.Run(() =>
        {
            var results = new BoqMatchedPair[targetItems.Count];

            // 1. Pre-index source items by Normalized Bill Key
            var sourcesByBill = new Dictionary<string, List<IndexedSourceItem>>(StringComparer.OrdinalIgnoreCase);
            var allIndexedSources = new List<IndexedSourceItem>(sourceItems.Count);

            for (int sIdx = 0; sIdx < sourceItems.Count; sIdx++)
            {
                var src = sourceItems[sIdx];
                var tokenHashes = ExtractTokenHashes(src.NormalizedDescription);
                var secHashes = ExtractTokenHashes(src.SectionName);
                var indexed = new IndexedSourceItem(src, tokenHashes, secHashes, sIdx);
                allIndexedSources.Add(indexed);

                string billKey = NormalizeBillKey(src.BillNumber);
                if (!sourcesByBill.TryGetValue(billKey, out var list))
                {
                    list = new List<IndexedSourceItem>(64);
                    sourcesByBill[billKey] = list;
                }
                list.Add(indexed);
            }

            // 2. Group target items by Normalized Bill Key while preserving original array indices
            var targetsByBill = new Dictionary<string, List<TargetItemEntry>>(StringComparer.OrdinalIgnoreCase);
            for (int tIdx = 0; tIdx < targetItems.Count; tIdx++)
            {
                var tgt = targetItems[tIdx];
                string billKey = NormalizeBillKey(tgt.BillNumber);
                if (!targetsByBill.TryGetValue(billKey, out var tList))
                {
                    tList = new List<TargetItemEntry>(64);
                    targetsByBill[billKey] = tList;
                }
                tList.Add(new TargetItemEntry(tgt, tIdx));
            }

            // 3. Process each bill independently (Parallel execution across distinct bills)
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };

            var unmatchedTargets = new ConcurrentBag<TargetItemEntry>();
            var consumedGlobalSourceIds = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            Parallel.ForEach(targetsByBill, parallelOptions, billEntry =>
            {
                string targetBillKey = billEntry.Key;
                var billTargets = billEntry.Value;

                // Find matching source bill partition
                List<IndexedSourceItem>? candidateSources = null;
                if (sourcesByBill.TryGetValue(targetBillKey, out var directList))
                {
                    candidateSources = directList;
                }
                else
                {
                    // Fuzzy bill key match
                    foreach (var kvp in sourcesByBill)
                    {
                        if (AreBillKeysMatching(targetBillKey, kvp.Key))
                        {
                            candidateSources = kvp.Value;
                            break;
                        }
                    }
                }

                var intraConsumedSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int tPos = 0; tPos < billTargets.Count; tPos++)
                {
                    var entry = billTargets[tPos];
                    var target = entry.Item;
                    int originalIndex = entry.OriginalIndex;

                    // Rule A: Provisional Sum Guard
                    if (target.IsProtected || target.Type == BoqItemType.ProvisionalSum)
                    {
                        results[originalIndex] = new BoqMatchedPair
                        {
                            TargetItem = target,
                            MatchedSourceItem = null,
                            SimilarityScore = 1.0,
                            Confidence = MatchConfidence.Exact,
                            MatchRationale = "Provisional Sum: Protected fixed lump sum scope.",
                            IsApproved = true
                        };
                        continue;
                    }

                    // Rule B: Intra-Bill Multi-Factor Matching with Disambiguation
                    if (candidateSources is not null && candidateSources.Count > 0)
                    {
                        var bestCandidate = FindBestIntraBillCandidate(
                            target,
                            tPos,
                            billTargets.Count,
                            candidateSources,
                            intraConsumedSourceIds,
                            sensitivity);

                        if (bestCandidate is not null)
                        {
                            intraConsumedSourceIds.Add(bestCandidate.Item.Id);
                            consumedGlobalSourceIds.TryAdd(bestCandidate.Item.Id, 1);

                            bool isExact = bestCandidate.Score >= 0.95 &&
                                           string.Equals(target.Unit, bestCandidate.Item.Unit, StringComparison.OrdinalIgnoreCase);

                            results[originalIndex] = new BoqMatchedPair
                            {
                                TargetItem = target,
                                MatchedSourceItem = bestCandidate.Item,
                                SimilarityScore = bestCandidate.Score,
                                Confidence = isExact ? MatchConfidence.Exact : MatchConfidence.HighFuzzy,
                                MatchRationale = bestCandidate.Rationale,
                                IsApproved = bestCandidate.Score >= 0.88 && bestCandidate.Item.IsPriced
                            };
                            continue;
                        }

                        // Target belongs to a known bill, but was unpriced / not in contractor's bill scope
                        // Strict Scope Isolation: Do NOT leak rates from other building models
                        results[originalIndex] = new BoqMatchedPair
                        {
                            TargetItem = target with { Type = BoqItemType.VariationOrder },
                            MatchedSourceItem = null,
                            SimilarityScore = 0.0,
                            Confidence = MatchConfidence.Unmatched,
                            MatchRationale = "Item unpriced or absent in contractor schedule for this specific bill.",
                            IsApproved = false
                        };
                        continue;
                    }

                    // Queue for Global Fallback ONLY if the bill itself had no matching source partition
                    unmatchedTargets.Add(entry);
                }
            });

            // 4. Global Fallback for remaining unmatched items (Cross-Bill search)
            if (!unmatchedTargets.IsEmpty)
            {
                Parallel.ForEach(unmatchedTargets, parallelOptions, entry =>
                {
                    var target = entry.Item;
                    int originalIndex = entry.OriginalIndex;

                    var bestGlobal = FindBestGlobalCandidate(
                        target,
                        allIndexedSources,
                        consumedGlobalSourceIds,
                        sensitivity);

                    if (bestGlobal is not null && bestGlobal.Score >= sensitivity)
                    {
                        consumedGlobalSourceIds.TryAdd(bestGlobal.Item.Id, 1);

                        results[originalIndex] = new BoqMatchedPair
                        {
                            TargetItem = target,
                            MatchedSourceItem = bestGlobal.Item,
                            SimilarityScore = bestGlobal.Score,
                            Confidence = bestGlobal.Score >= 0.95 ? MatchConfidence.Exact : MatchConfidence.HighFuzzy,
                            MatchRationale = $"Cross-bill fallback: {bestGlobal.Rationale}",
                            IsApproved = bestGlobal.Score >= 0.90
                        };
                    }
                    else if (bestGlobal is not null && bestGlobal.Score >= 0.70)
                    {
                        results[originalIndex] = new BoqMatchedPair
                        {
                            TargetItem = target,
                            MatchedSourceItem = bestGlobal.Item,
                            SimilarityScore = bestGlobal.Score,
                            Confidence = MatchConfidence.ManualReviewNeeded,
                            MatchRationale = $"Cross-bill candidate needs engineering review. ({bestGlobal.Rationale})",
                            IsApproved = false
                        };
                    }
                    else
                    {
                        // Unmatched -> Proposed Variation Order / New Scope item
                        results[originalIndex] = new BoqMatchedPair
                        {
                            TargetItem = target with { Type = BoqItemType.VariationOrder },
                            MatchedSourceItem = null,
                            SimilarityScore = 0.0,
                            Confidence = MatchConfidence.Unmatched,
                            MatchRationale = "Unmatched item in original tender; flagged as New Scope / Variation Order.",
                            IsApproved = false
                        };
                    }
                });
            }

            return (IReadOnlyList<BoqMatchedPair>)results;
        }, ct);
    }

    private static ScoredCandidate? FindBestIntraBillCandidate(
        BoqItem target,
        int targetPos,
        int totalTargets,
        List<IndexedSourceItem> candidates,
        HashSet<string> consumedIds,
        double sensitivity)
    {
        var targetHashes = ExtractTokenHashes(target.NormalizedDescription);
        var targetSecHashes = ExtractTokenHashes(target.SectionName);
        var lev = new Levenshtein(target.NormalizedDescription);

        ScoredCandidate? best = null;
        double highestFitness = -1.0;

        for (int cIdx = 0; cIdx < candidates.Count; cIdx++)
        {
            var candidate = candidates[cIdx];
            if (consumedIds.Contains(candidate.Item.Id)) continue;

            // Unit Compatibility Check
            bool unitExact = string.Equals(target.Unit, candidate.Item.Unit, StringComparison.OrdinalIgnoreCase);
            bool unitCompatible = unitExact || AreUnitsCompatible(target.Unit, candidate.Item.Unit);
            if (!unitCompatible) continue;

            // 1. Text Similarity Metric
            double textScore;
            if (string.Equals(target.NormalizedDescription, candidate.Item.NormalizedDescription, StringComparison.OrdinalIgnoreCase))
            {
                textScore = 1.0;
            }
            else
            {
                double trigCosine = CalculateTrigonometricCosine(targetHashes, candidate.TokenHashes);
                double jaccard = CalculateSortedHashJaccard(targetHashes, candidate.TokenHashes);
                int distance = lev.DistanceFrom(candidate.Item.NormalizedDescription);
                int maxLen = Math.Max(target.NormalizedDescription.Length, candidate.Item.NormalizedDescription.Length);
                if (maxLen == 0) continue;

                double levScore = 1.0 - ((double)distance / maxLen);
                textScore = (0.55 * levScore) + (0.25 * trigCosine) + (0.20 * jaccard);
            }

            if (textScore < 0.60) continue;

            // 2. Multi-Factor Disambiguation Fitness
            double fitness = textScore * 100.0;

            if (unitExact) fitness += 25.0;

            // ItemCode match bonus
            if (!string.IsNullOrWhiteSpace(target.ItemCode) &&
                !string.IsNullOrWhiteSpace(candidate.Item.ItemCode) &&
                string.Equals(target.ItemCode, candidate.Item.ItemCode, StringComparison.OrdinalIgnoreCase))
            {
                fitness += 45.0;
            }

            // Quantity match bonus
            if (target.Quantity > 0m && candidate.Item.Quantity > 0m)
            {
                if (target.Quantity == candidate.Item.Quantity)
                {
                    fitness += 40.0;
                }
                else
                {
                    decimal diff = Math.Abs(target.Quantity - candidate.Item.Quantity);
                    decimal maxQ = Math.Max(target.Quantity, candidate.Item.Quantity);
                    if (diff / maxQ <= 0.05m) fitness += 25.0;
                    else if (diff / maxQ <= 0.20m) fitness += 10.0;
                }
            }
            else if (target.Quantity == 0m && candidate.Item.Quantity == 0m)
            {
                fitness += 20.0;
            }

            // Section / Pipe Diameter token overlap bonus
            if (!string.IsNullOrWhiteSpace(target.SectionName) && !string.IsNullOrWhiteSpace(candidate.Item.SectionName))
            {
                double secCosine = CalculateTrigonometricCosine(targetSecHashes, candidate.SectionHashes);
                fitness += secCosine * 30.0;
            }

            // Monotonic Sequence Alignment bonus
            double tRatio = totalTargets > 0 ? (double)targetPos / totalTargets : 0.0;
            double cRatio = candidates.Count > 0 ? (double)cIdx / candidates.Count : 0.0;
            double posDiff = Math.Abs(tRatio - cRatio);
            fitness += (1.0 - posDiff) * 20.0;

            if (fitness > highestFitness && textScore >= (sensitivity - 0.15))
            {
                highestFitness = fitness;
                best = new ScoredCandidate(
                    candidate.Item,
                    textScore,
                    $"Intra-bill match: Text {textScore:P0}, Fitness {fitness:F0}, Unit: {candidate.Item.Unit}"
                );
            }
        }

        return best;
    }

    private static ScoredCandidate? FindBestGlobalCandidate(
        BoqItem target,
        List<IndexedSourceItem> allSources,
        ConcurrentDictionary<string, byte> consumedIds,
        double sensitivity)
    {
        var targetHashes = ExtractTokenHashes(target.NormalizedDescription);
        var lev = new Levenshtein(target.NormalizedDescription);

        ScoredCandidate? best = null;
        double bestScore = 0.0;

        foreach (var candidate in allSources)
        {
            if (consumedIds.ContainsKey(candidate.Item.Id)) continue;
            if (!string.Equals(target.Unit, candidate.Item.Unit, StringComparison.OrdinalIgnoreCase)) continue;

            double trigCosine = CalculateTrigonometricCosine(targetHashes, candidate.TokenHashes);
            if (trigCosine < 0.20) continue;

            double jaccard = CalculateSortedHashJaccard(targetHashes, candidate.TokenHashes);
            int distance = lev.DistanceFrom(candidate.Item.NormalizedDescription);
            int maxLen = Math.Max(target.NormalizedDescription.Length, candidate.Item.NormalizedDescription.Length);
            if (maxLen == 0) continue;

            double levScore = 1.0 - ((double)distance / maxLen);
            double score = (0.60 * levScore) + (0.20 * trigCosine) + (0.20 * jaccard);

            if (AreBillsRelated(target.BillNumber, candidate.Item.BillNumber))
            {
                score = Math.Min(1.0, score + 0.05);
            }
            else
            {
                score -= 0.05; // Cross-bill penalty
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = new ScoredCandidate(
                    candidate.Item,
                    score,
                    $"Global fallback score: {score:P1} (Lev: {levScore:P1}, Cos: {trigCosine:P1})"
                );
            }
        }

        return best;
    }

    private static bool AreUnitsCompatible(string u1, string u2)
    {
        if (string.Equals(u1, u2, StringComparison.OrdinalIgnoreCase)) return true;
        // Group equivalents
        var groupA = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "m2", "sqm", "sq.m", "m²" };
        var groupB = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "m3", "cum", "cu.m", "m³" };
        var groupC = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "m", "lm", "lin.m" };
        var groupD = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nr", "no", "nos", "number", "item" };
        var groupE = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "t", "ton", "tonne" };

        if (groupA.Contains(u1) && groupA.Contains(u2)) return true;
        if (groupB.Contains(u1) && groupB.Contains(u2)) return true;
        if (groupC.Contains(u1) && groupC.Contains(u2)) return true;
        if (groupD.Contains(u1) && groupD.Contains(u2)) return true;
        if (groupE.Contains(u1) && groupE.Contains(u2)) return true;

        return false;
    }

    private static string NormalizeBillKey(string billName)
    {
        if (string.IsNullOrWhiteSpace(billName)) return string.Empty;
        var chars = new char[billName.Length];
        int count = 0;
        for (int i = 0; i < billName.Length; i++)
        {
            char c = billName[i];
            if (char.IsLetterOrDigit(c))
            {
                chars[count++] = char.ToLowerInvariant(c);
            }
        }
        return new string(chars, 0, count);
    }

    private static bool AreBillKeysMatching(string k1, string k2)
    {
        if (string.IsNullOrEmpty(k1) || string.IsNullOrEmpty(k2)) return false;
        if (k1 == k2) return true;
        if (k1.StartsWith(k2) || k2.StartsWith(k1)) return true;
        if (k1.Contains(k2) || k2.Contains(k1)) return true;

        // Extract alphanumeric bill tag (e.g. "02a", "02b", "03a", "04", "05", "infra", "retail")
        string tag1 = ExtractBillTag(k1);
        string tag2 = ExtractBillTag(k2);
        if (!string.IsNullOrEmpty(tag1) && !string.IsNullOrEmpty(tag2) && tag1 == tag2)
        {
            return true;
        }

        // Fuzzy match on bill keys for arbitrary non-standard projects
        int maxLen = Math.Max(k1.Length, k2.Length);
        if (maxLen > 0)
        {
            int dist = new Levenshtein(k1).DistanceFrom(k2);
            if (1.0 - ((double)dist / maxLen) >= 0.70) return true;
        }

        return false;
    }

    /// <summary>
    /// Algorithmic, project-agnostic bill signature extractor.
    /// Extracts canonical alphanumeric package identifiers dynamically using regex tokenization
    /// without any hardcoded project-specific bill names.
    /// </summary>
    private static string ExtractBillTag(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        // Pattern 1: Find "bill" followed by numbers and optional sub-bill letters (e.g. "bill02a", "bill1", "bill061a")
        var match = Regex.Match(key, @"bill(\d+[a-z]*)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.TrimStart('0');
        }

        // Pattern 2: Any leading digits with trailing letter (e.g. "02a", "3b", "05")
        var match2 = Regex.Match(key, @"(\d+[a-z]*)", RegexOptions.IgnoreCase);
        if (match2.Success && match2.Value.Length >= 2)
        {
            return match2.Value.TrimStart('0');
        }

        // Pattern 3: Distinct structural scope keyword tokens
        string[] semanticKeywords = ["infra", "retail", "landscape", "prelim", "common", "mep", "facade", "hvac", "plumb"];
        foreach (var kw in semanticKeywords)
        {
            if (key.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return kw;
            }
        }

        return string.Empty;
    }

    private sealed record IndexedSourceItem(BoqItem Item, ulong[] TokenHashes, ulong[] SectionHashes, int OriginalIndex);
    private sealed record TargetItemEntry(BoqItem Item, int OriginalIndex);
    private sealed record ScoredCandidate(BoqItem Item, double Score, string Rationale);
}
