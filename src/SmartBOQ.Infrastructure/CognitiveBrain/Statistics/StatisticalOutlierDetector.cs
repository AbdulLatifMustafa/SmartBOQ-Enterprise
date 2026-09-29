using MathNet.Numerics.Statistics;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Statistics;

/// <summary>
/// Result of an automated statistical audit on unit rates and financial amounts.
/// </summary>
public sealed record RateAnomalyReport
{
    public required BoqItem Item { get; init; }
    public decimal UnitRate { get; init; }
    public double ModifiedZScore { get; init; }
    public bool IsRateOutlier { get; init; }
    public bool IsDecimalShiftSuspected { get; init; }
    public bool IsMathInconsistent { get; init; }
    public string AnomalyDescription { get; init; } = string.Empty;
}

/// <summary>
/// Mathematical and statistical auditor using MathNet.Numerics for anomaly and outlier detection.
/// Employs Median Absolute Deviation (MAD), Modified Z-Score, and Interquartile Range (IQR).
/// </summary>
public sealed class StatisticalOutlierDetector
{
    /// <summary>
    /// Analyzes an array of unit rates and identifies statistical outliers and decimal shift errors.
    /// </summary>
    public IReadOnlyList<RateAnomalyReport> AuditRates(IReadOnlyList<BoqItem> items)
    {
        var pricedItems = items.Where(i => i.UnitRate.HasValue && i.UnitRate.Value > 0).ToList();
        if (pricedItems.Count < 4)
        {
            return Array.Empty<RateAnomalyReport>();
        }

        // Convert decimal rates to double for MathNet.Numerics processing
        var rates = pricedItems.Select(i => (double)i.UnitRate!.Value).ToArray();

        double median = rates.Median();
        double mad = rates.Select(x => Math.Abs(x - median)).ToArray().Median();
        double mean = rates.Mean();
        double stdDev = rates.StandardDeviation();
        double iqr = rates.InterquartileRange();

        var reports = new List<RateAnomalyReport>();

        for (int i = 0; i < pricedItems.Count; i++)
        {
            var item = pricedItems[i];
            double r = rates[i];

            // Modified Z-score calculation: 0.6745 * (x - median) / MAD with stdDev fallback when MAD=0
            double modZ = mad > 0.0001 
                ? 0.6745 * (r - median) / mad 
                : (stdDev > 0.0001 ? (r - mean) / stdDev : 0.0);
            bool isOutlier = Math.Abs(modZ) >= 3.5;

            // Decimal shift detection: ratio approximately 10x, 100x, 1000x, 0.1x, or 0.01x of median
            bool isDecimalShift = false;
            if (median > 0)
            {
                double ratio = r / median;
                if ((ratio >= 9.5 && ratio <= 10.5) || 
                    (ratio >= 95.0 && ratio <= 105.0) || 
                    (ratio >= 950.0 && ratio <= 1050.0) ||
                    (ratio >= 0.095 && ratio <= 0.105) ||
                    (ratio >= 0.0095 && ratio <= 0.0105))
                {
                    isDecimalShift = true;
                }
            }

            // Mathematical consistency: Check Amount == Qty * Rate
            bool isMathInconsistent = false;
            if (item.TotalAmount.HasValue && item.Quantity > 0 && item.UnitRate.HasValue)
            {
                decimal expected = Math.Round(item.Quantity * item.UnitRate.Value, 2);
                decimal actual = Math.Round(item.TotalAmount.Value, 2);
                if (Math.Abs(expected - actual) > 1.0m) // Discrepancy > 1 EGP
                {
                    isMathInconsistent = true;
                }
            }

            if (isOutlier || isDecimalShift || isMathInconsistent)
            {
                string desc = "";
                if (isDecimalShift) desc = $"Potential decimal misplacement (Ratio {r / median:F1}x vs benchmark). ";
                else if (isOutlier) desc = $"Rate variance outlier (|ModZ|={Math.Abs(modZ):F1} > 3.5). ";
                if (isMathInconsistent) desc += $"Amount ({item.TotalAmount}) differs from Qty*Rate ({item.Quantity * item.UnitRate}).";

                reports.Add(new RateAnomalyReport
                {
                    Item = item,
                    UnitRate = item.UnitRate!.Value,
                    ModifiedZScore = modZ,
                    IsRateOutlier = isOutlier,
                    IsDecimalShiftSuspected = isDecimalShift,
                    IsMathInconsistent = isMathInconsistent,
                    AnomalyDescription = desc.Trim()
                });
            }
        }

        return reports;
    }

    /// <summary>
    /// Analyzes unit rates stratified by engineering dimension class (Volume, Area, Weight, Count, etc.).
    /// Prevents cross-trade false alarms (e.g. comparing bulk excavation m3 rates with generator lump-sum rates).
    /// </summary>
    public IReadOnlyList<RateAnomalyReport> AuditRatesStratified(IReadOnlyList<BoqItem> items)
    {
        var pricedItems = items.Where(i => i.UnitRate.HasValue && i.UnitRate.Value > 0).ToList();
        if (pricedItems.Count < 4)
        {
            return Array.Empty<RateAnomalyReport>();
        }

        // Group by physical dimension class
        var dimGroups = pricedItems.GroupBy(i => QuantileScaleLattice.ClassifyUnit(i.Unit)).ToList();
        var allReports = new List<RateAnomalyReport>();

        foreach (var group in dimGroups)
        {
            var groupItems = group.ToList();
            if (groupItems.Count >= 4)
            {
                var groupReports = AuditRates(groupItems);
                allReports.AddRange(groupReports);
            }
        }

        // Check math inconsistency for any items not covered above
        var reportedItemIds = new HashSet<string>(allReports.Select(r => r.Item.Id));
        foreach (var item in pricedItems)
        {
            if (!reportedItemIds.Contains(item.Id) && item.TotalAmount.HasValue && item.Quantity > 0 && item.UnitRate.HasValue)
            {
                decimal expected = Math.Round(item.Quantity * item.UnitRate.Value, 2);
                decimal actual = Math.Round(item.TotalAmount.Value, 2);
                if (Math.Abs(expected - actual) > 1.0m)
                {
                    allReports.Add(new RateAnomalyReport
                    {
                        Item = item,
                        UnitRate = item.UnitRate.Value,
                        ModifiedZScore = 0,
                        IsRateOutlier = false,
                        IsDecimalShiftSuspected = false,
                        IsMathInconsistent = true,
                        AnomalyDescription = $"Amount ({item.TotalAmount}) differs from Qty*Rate ({item.Quantity * item.UnitRate})."
                    });
                }
            }
        }

        return allReports;
    }
}
