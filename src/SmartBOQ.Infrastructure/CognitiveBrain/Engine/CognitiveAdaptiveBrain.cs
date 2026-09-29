using System.Collections.Concurrent;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.CognitiveBrain.Currencies;
using SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;
using SmartBOQ.Infrastructure.CognitiveBrain.Statistics;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Engine;

/// <summary>
/// Cognitive Adaptive Reconciliation and Comparison Brain.
/// Completely sheet-name agnostic, multi-currency aware, SIMD-accelerated,
/// and powered by global bipartite optimal matching and statistical anomaly detection.
/// Implements <see cref="IItemMatcher"/> for seamless harmony with the entire SmartBOQ architecture.
/// </summary>
public sealed class CognitiveAdaptiveBrain : IItemMatcher
{
    private readonly MultiCurrencyCognitiveArbitrator _currencyArbitrator;
    private readonly StatisticalOutlierDetector _outlierDetector;

    public MultiCurrencyCognitiveArbitrator CurrencyArbitrator => _currencyArbitrator;
    public StatisticalOutlierDetector OutlierDetector => _outlierDetector;

    public CognitiveAdaptiveBrain(
        MultiCurrencyCognitiveArbitrator? currencyArbitrator = null,
        StatisticalOutlierDetector? outlierDetector = null)
    {
        _currencyArbitrator = currencyArbitrator ?? new MultiCurrencyCognitiveArbitrator();
        _outlierDetector = outlierDetector ?? new StatisticalOutlierDetector();
    }

    /// <summary>
    /// Executes intelligent, sheet-agnostic multi-dimensional matching between target and source BOQ items.
    /// </summary>
    public Task<IReadOnlyList<BoqMatchedPair>> MatchItemsAsync(
        IReadOnlyList<BoqItem> targetItems,
        IReadOnlyList<BoqItem> sourceItems,
        double sensitivity = 0.85,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (targetItems.Count == 0)
            {
                return (IReadOnlyList<BoqMatchedPair>)Array.Empty<BoqMatchedPair>();
            }

            var results = new BoqMatchedPair[targetItems.Count];

            // Filter priced candidate sources
            var validSources = new List<BoqItem>(sourceItems.Count);
            for (int i = 0; i < sourceItems.Count; i++)
            {
                var s = sourceItems[i];
                decimal r = s.UnitRate ?? (s.OriginalRate ?? 0m);
                bool isIgnored = s.Description.Contains("Ignored", StringComparison.OrdinalIgnoreCase) ||
                                 s.LineItemText.Contains("Ignored", StringComparison.OrdinalIgnoreCase);

                if (r > 0m || isIgnored)
                {
                    validSources.Add(s);
                }
            }

            // If no priced source items exist, preserve target baseline tender rates
            if (validSources.Count == 0)
            {
                for (int tIdx = 0; tIdx < targetItems.Count; tIdx++)
                {
                    results[tIdx] = CreateBaselineFallbackPair(targetItems[tIdx]);
                }
                return (IReadOnlyList<BoqMatchedPair>)results;
            }

            // 1. Build Multi-Dimensional Metric Index on Source Items
            var metricIndex = new MultiDimensionalMetricIndex(validSources);

            // 2. Pre-compute Target Signatures and Fast-Path Index
            var targetSignatures = new ItemContentSignature[targetItems.Count];
            var fastPathSourceLookup = new Dictionary<ulong, int>();
            for (int sIdx = 0; sIdx < validSources.Count; sIdx++)
            {
                var sig = new ItemContentSignature(validSources[sIdx], _currencyArbitrator);
                fastPathSourceLookup.TryAdd(sig.FastPathHash, sIdx);
            }

            bool hasCodes = false;
            bool hasDistinctQuantities = false;
            decimal firstQty = -1m;

            for (int tIdx = 0; tIdx < targetItems.Count; tIdx++)
            {
                var sig = new ItemContentSignature(targetItems[tIdx], _currencyArbitrator);
                targetSignatures[tIdx] = sig;
                if (!string.IsNullOrEmpty(sig.NormalizedCode)) hasCodes = true;
                if (firstQty < 0m) firstQty = sig.Quantity;
                else if (sig.Quantity != firstQty) hasDistinctQuantities = true;
            }

            var profile = CognitiveScoringProfile.CreateAdaptive(hasCodes, hasDistinctQuantities);

            // 3. Construct Bipartite Matching Graph
            var bipartiteGraph = new BipartiteMatchingGraph(targetItems.Count, validSources.Count, targetItems.Count * 16);
            var fastPathMatchedTargets = new HashSet<int>();

            // Phase 0 & 1: Fast-Path Evaluation and Metric Candidate Generation
            for (int tIdx = 0; tIdx < targetItems.Count; tIdx++)
            {
                ct.ThrowIfCancellationRequested();

                var tgtSig = targetSignatures[tIdx];
                var target = tgtSig.OriginalItem;

                // Rule A: Provisional Sum Guard
                if (target.IsProtected || target.Type == BoqItemType.ProvisionalSum)
                {
                    results[tIdx] = new BoqMatchedPair
                    {
                        TargetItem = target,
                        MatchedSourceItem = null,
                        SimilarityScore = 1.0,
                        Confidence = MatchConfidence.Exact,
                        MatchRationale = "Provisional Sum: Protected fixed lump sum scope.",
                        IsApproved = true
                    };
                    fastPathMatchedTargets.Add(tIdx);
                    continue;
                }

                // Rule B: Instant O(1) Fast-Path Identical Match
                if (fastPathSourceLookup.TryGetValue(tgtSig.FastPathHash, out int exactSourceIdx))
                {
                    var exactSource = validSources[exactSourceIdx];
                    decimal rate = exactSource.UnitRate ?? (exactSource.OriginalRate ?? 0m);

                    results[tIdx] = new BoqMatchedPair
                    {
                        TargetItem = target,
                        MatchedSourceItem = exactSource,
                        InjectedRate = rate,
                        SimilarityScore = 1.0,
                        Confidence = MatchConfidence.Exact,
                        MatchRationale = "Cognitive fast-path: Identical content signature match.",
                        IsApproved = true
                    };
                    fastPathMatchedTargets.Add(tIdx);
                    continue;
                }

                // Rule C: Multi-Dimensional Metric Candidate Generation
                int[] candidates = metricIndex.FindCandidates(tgtSig.Tokens, tgtSig.NormalizedCode, tgtSig.Dimension, maxCandidates: 24);

                foreach (int sIdx in candidates)
                {
                    var srcMetric = metricIndex.Items[sIdx];
                    double score = EvaluateCandidateScore(tgtSig, srcMetric, profile);
                    if (score >= sensitivity * 0.70)
                    {
                        bipartiteGraph.AddEdge(tIdx, sIdx, score);
                    }
                }
            }

            // Phase 4: Solve Globally Optimal Bipartite Assignment
            var (targetToSource, matchScores) = bipartiteGraph.SolveOptimalAssignment();

            // Phase 5: Result Synthesis & Baseline Preservation
            for (int tIdx = 0; tIdx < targetItems.Count; tIdx++)
            {
                if (fastPathMatchedTargets.Contains(tIdx))
                {
                    continue; // Already resolved in fast-path
                }

                var target = targetItems[tIdx];
                int assignedSourceIdx = targetToSource[tIdx];
                double score = matchScores[tIdx];

                if (assignedSourceIdx >= 0 && score >= sensitivity)
                {
                    var sourceItem = validSources[assignedSourceIdx];
                    decimal sourceRate = sourceItem.UnitRate ?? (sourceItem.OriginalRate ?? 0m);

                    // Multi-Currency Arbitration: Check currency parity and scale drift
                    var tgtCur = targetSignatures[tIdx].Currency;
                    var srcCur = _currencyArbitrator.ResolveItemCurrency(sourceItem);
                    decimal finalRate = sourceRate;
                    string rationale = $"Cognitive match score: {score:P1}";

                    if (_currencyArbitrator.TryDetectCurrencyScaleDrift(target.OriginalRate ?? 0m, sourceRate, out var curA, out var curB, out decimal normalizedRate))
                    {
                        finalRate = normalizedRate;
                        rationale += $" [FX Parity calibrated: {curB}->{curA}]";
                    }
                    else if (srcCur != tgtCur && srcCur != CurrencyType.Unknown && tgtCur != CurrencyType.Unknown)
                    {
                        finalRate = _currencyArbitrator.NormalizeRate(sourceRate, srcCur, tgtCur);
                        rationale += $" [Converted {srcCur} to {tgtCur}]";
                    }

                    results[tIdx] = new BoqMatchedPair
                    {
                        TargetItem = target,
                        MatchedSourceItem = sourceItem,
                        InjectedRate = finalRate,
                        SimilarityScore = score,
                        Confidence = score >= 0.95 ? MatchConfidence.Exact : MatchConfidence.HighFuzzy,
                        MatchRationale = rationale,
                        IsApproved = true
                    };
                }
                else
                {
                    // Fallback: Preserve original tender baseline rate if present
                    results[tIdx] = CreateBaselineFallbackPair(target);
                }
            }

            return (IReadOnlyList<BoqMatchedPair>)results;
        }, ct);
    }

    /// <summary>
    /// Evaluates cognitive multi-dimensional affinity between a target signature and an indexed source item.
    /// </summary>
    private double EvaluateCandidateScore(ItemContentSignature target, IndexedMetricItem source, CognitiveScoringProfile profile)
    {
        // 0. Contractual Action Scope Exclusivity (e.g. Supply vs Install)
        if (SmartBOQ.Domain.Analysis.ContractualScopeClassifier.AreScopesMutuallyExclusive(target.Scope, source.Scope))
        {
            return 0.0; // Strictly reject cross-scope matches
        }

        double scopeMultiplier = SmartBOQ.Domain.Analysis.ContractualScopeClassifier.GetScopeCompatibilityMultiplier(target.Scope, source.Scope);

        double codeScore = 0.0;
        if (!string.IsNullOrEmpty(target.NormalizedCode) && !string.IsNullOrEmpty(source.NormalizedCode))
        {
            if (target.NormalizedCode.Equals(source.NormalizedCode, StringComparison.OrdinalIgnoreCase))
            {
                codeScore = 1.0;
            }
            else if (target.NormalizedCode.StartsWith(source.NormalizedCode, StringComparison.OrdinalIgnoreCase) ||
                     source.NormalizedCode.StartsWith(target.NormalizedCode, StringComparison.OrdinalIgnoreCase))
            {
                codeScore = 0.8;
            }
        }

        // Token coverage & Jaccard overlap
        double tokenCoverage = target.Tokens.CalculateTokenCoverage(source.TokenSet);
        double jaccard = target.Tokens.CalculateJaccard(source.TokenSet);
        double descScore = (tokenCoverage * 0.7) + (jaccard * 0.3);

        // Unit dimensional compatibility
        double unitScore = (target.Dimension != DimensionClass.Unknown && target.Dimension == source.UnitDimension) ? 1.0 : 0.4;

        // Quantity proportion alignment
        double qtyScore = QuantileScaleLattice.EvaluateQuantityProportion(target.Quantity, source.Item.Quantity, out _);

        // Price harmony factor
        double priceScore = 0.5;
        if (target.Rate.HasValue && source.NormalizedRate > 0)
        {
            decimal min = Math.Min(target.Rate.Value, source.NormalizedRate);
            decimal max = Math.Max(target.Rate.Value, source.NormalizedRate);
            if (max > 0m)
            {
                priceScore = (double)(min / max);
            }
        }
        else if (target.Rate.HasValue && target.Rate.Value == 0m && source.NormalizedRate == 0m)
        {
            priceScore = 1.0;
        }

        double totalScore = (codeScore * profile.WeightCode) +
                            (descScore * profile.WeightDescription) +
                            (unitScore * profile.WeightUnit) +
                            (qtyScore * profile.WeightQuantity) +
                            (priceScore * profile.WeightPriceHarmony);

        // Code exact match bonus
        if (codeScore >= 0.95 && descScore >= 0.40)
        {
            totalScore += 0.20;
        }

        totalScore *= scopeMultiplier;

        return Math.Min(1.0, totalScore);
    }

    private static BoqMatchedPair CreateBaselineFallbackPair(BoqItem target)
    {
        if (target.OriginalRate.HasValue && target.OriginalRate.Value > 0)
        {
            return new BoqMatchedPair
            {
                TargetItem = target,
                MatchedSourceItem = null,
                InjectedRate = target.OriginalRate.Value,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                MatchRationale = "Approved baseline tender rate preserved.",
                IsApproved = true
            };
        }

        if (target.OriginalRate.HasValue && target.OriginalRate.Value == 0m)
        {
            return new BoqMatchedPair
            {
                TargetItem = target,
                MatchedSourceItem = null,
                InjectedRate = 0m,
                SimilarityScore = 1.0,
                Confidence = MatchConfidence.Exact,
                MatchRationale = "Scope Ignored / Zero-rate confirmed as per tender.",
                IsApproved = true
            };
        }

        return new BoqMatchedPair
        {
            TargetItem = target with { Type = BoqItemType.VariationOrder },
            MatchedSourceItem = null,
            SimilarityScore = 0.0,
            Confidence = MatchConfidence.Unmatched,
            MatchRationale = "Unmatched item in original tender; flagged as New Scope / Variation Order.",
            IsApproved = false
        };
    }
}
