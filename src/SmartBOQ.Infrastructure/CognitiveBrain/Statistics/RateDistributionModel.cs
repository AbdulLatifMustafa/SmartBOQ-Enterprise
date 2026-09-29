using MathNet.Numerics.Statistics;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Statistics;

/// <summary>
/// Price percentile distribution model for engineering tender analysis.
/// </summary>
public sealed record PriceDistributionSummary
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Median { get; init; }
    public double Mean { get; init; }
    public double P10 { get; init; }
    public double P25 { get; init; }
    public double P75 { get; init; }
    public double P90 { get; init; }
    public double StandardDeviation { get; init; }
}

public static class RateDistributionModel
{
    public static PriceDistributionSummary ComputeDistribution(IEnumerable<decimal> rates)
    {
        var doubles = rates.Where(r => r > 0m).Select(r => (double)r).ToArray();
        if (doubles.Length == 0)
        {
            return new PriceDistributionSummary();
        }

        Array.Sort(doubles);

        return new PriceDistributionSummary
        {
            Min = doubles[0],
            Max = doubles[^1],
            Median = doubles.Median(),
            Mean = doubles.Mean(),
            P10 = doubles.Percentile(10),
            P25 = doubles.Percentile(25),
            P75 = doubles.Percentile(75),
            P90 = doubles.Percentile(90),
            StandardDeviation = doubles.Length > 1 ? doubles.StandardDeviation() : 0.0
        };
    }
}
