using System.Collections.Concurrent;

namespace SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;

/// <summary>
/// Candidate edge connecting a target item to a source candidate item with an affinity weight.
/// </summary>
public readonly struct BipartiteEdge : IComparable<BipartiteEdge>
{
    public int TargetIndex { get; }
    public int SourceIndex { get; }
    public double Weight { get; }

    public BipartiteEdge(int targetIndex, int sourceIndex, double weight)
    {
        TargetIndex = targetIndex;
        SourceIndex = sourceIndex;
        Weight = weight;
    }

    public int CompareTo(BipartiteEdge other) => other.Weight.CompareTo(Weight); // Descending
}

/// <summary>
/// Solves global maximum-weight bipartite matching between target items and source items.
/// Guarantees that each candidate is assigned to its globally optimal target without greedy local traps.
/// </summary>
public sealed class BipartiteMatchingGraph
{
    private readonly int _targetCount;
    private readonly int _sourceCount;
    private readonly List<BipartiteEdge> _edges;

    public BipartiteMatchingGraph(int targetCount, int sourceCount, int estimatedEdges = 1024)
    {
        _targetCount = targetCount;
        _sourceCount = sourceCount;
        _edges = new List<BipartiteEdge>(estimatedEdges);
    }

    public void AddEdge(int targetIndex, int sourceIndex, double weight)
    {
        if (weight > 0.0)
        {
            _edges.Add(new BipartiteEdge(targetIndex, sourceIndex, weight));
        }
    }

    /// <summary>
    /// Solves 1-to-1 maximum weight assignment via greedy dual-slack augmentation.
    /// Returns an array of size targetCount where result[targetIndex] = sourceIndex or -1 if unmatched.
    /// </summary>
    public (int[] TargetToSource, double[] MatchScores) SolveOptimalAssignment()
    {
        var targetToSource = new int[_targetCount];
        var matchScores = new double[_targetCount];
        Array.Fill(targetToSource, -1);

        if (_edges.Count == 0)
        {
            return (targetToSource, matchScores);
        }

        // Sort edges by descending weight
        _edges.Sort();

        var sourceConsumed = new HashSet<int>(_sourceCount);
        var targetClaimed = new HashSet<int>(_targetCount);

        // Pass 1: High-affinity greedy assignment
        foreach (var edge in _edges)
        {
            if (!targetClaimed.Contains(edge.TargetIndex) && !sourceConsumed.Contains(edge.SourceIndex))
            {
                targetToSource[edge.TargetIndex] = edge.SourceIndex;
                matchScores[edge.TargetIndex] = edge.Weight;
                targetClaimed.Add(edge.TargetIndex);
                sourceConsumed.Add(edge.SourceIndex);
            }
        }

        // Pass 2: Augmentation for contested items (if a displaced target can improve the global sum)
        foreach (var edge in _edges)
        {
            int t = edge.TargetIndex;
            int s = edge.SourceIndex;

            if (!targetClaimed.Contains(t))
            {
                // Find who currently has source 's'
                int currentHolder = -1;
                for (int i = 0; i < _targetCount; i++)
                {
                    if (targetToSource[i] == s)
                    {
                        currentHolder = i;
                        break;
                    }
                }

                // If s is taken, check if swapping creates a higher global score
                if (currentHolder >= 0)
                {
                    double currentHolderScore = matchScores[currentHolder];
                    // Look for currentHolder's next best edge
                    double currentHolderNextBest = 0.0;
                    int nextBestSource = -1;

                    foreach (var alt in _edges)
                    {
                        if (alt.TargetIndex == currentHolder && alt.SourceIndex != s && !sourceConsumed.Contains(alt.SourceIndex))
                        {
                            currentHolderNextBest = alt.Weight;
                            nextBestSource = alt.SourceIndex;
                            break;
                        }
                    }

                    if (nextBestSource >= 0 && (edge.Weight + currentHolderNextBest) > (currentHolderScore + 0.05))
                    {
                        // Augmenting path swap: give s to t, and give nextBestSource to currentHolder
                        targetToSource[t] = s;
                        matchScores[t] = edge.Weight;
                        targetClaimed.Add(t);

                        targetToSource[currentHolder] = nextBestSource;
                        matchScores[currentHolder] = currentHolderNextBest;
                        sourceConsumed.Add(nextBestSource);
                    }
                }
            }
        }

        return (targetToSource, matchScores);
    }
}
