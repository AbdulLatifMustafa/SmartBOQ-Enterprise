using System.Runtime.CompilerServices;

namespace SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;

/// <summary>
/// Physical engineering dimensional classes.
/// </summary>
public enum DimensionClass
{
    Unknown = 0,
    Volume = 1,     // m3, cu.m, liter
    Area = 2,       // m2, sqm, feddan
    Length = 3,     // m, lm, km, mm, inch
    Weight = 4,     // ton, kg, lb
    Count = 5,      // no, ea, nr, pcs, set, pair
    LumpSum = 6,    // ls, sum, item, job, مقطوعية
    Time = 7,       // month, day, hr, wk
    Power = 8       // kw, kva, hp, mw
}

/// <summary>
/// Mathematical scale lattice that evaluates dimensional compatibility, quantity ratios,
/// and scale multiplier alignment between BOQ items across different measurement systems.
/// </summary>
public static class QuantileScaleLattice
{
    /// <summary>
    /// Resolves engineering unit string to its underlying physical dimensional class.
    /// </summary>
    public static DimensionClass ClassifyUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return DimensionClass.Unknown;
        string u = unit.Trim().ToLowerInvariant()
            .Replace(".", "")
            .Replace(" ", "");

        // Volume
        if (u is "m3" or "cum" or "m³" or "cubicmeter" or "م3" or "مترمكعب" or "لتر" or "liter" or "l")
            return DimensionClass.Volume;

        // Area
        if (u is "m2" or "sqm" or "m²" or "sqmeter" or "م2" or "مترمربع" or "مسطح" or "فدان")
            return DimensionClass.Area;

        // Length
        if (u is "m" or "lm" or "ml" or "meter" or "linm" or "م" or "مط" or "مترطولي" or "متر" or "km" or "mm" or "cm" or "inch")
            return DimensionClass.Length;

        // Weight
        if (u is "ton" or "tonne" or "طن" or "kg" or "كجم" or "كيلوجرام" or "gm" or "lbs")
            return DimensionClass.Weight;

        // LumpSum / Contractual Note
        if (u is "ls" or "sum" or "item" or "lot" or "job" or "مقطوعية" or "بند" or "جملة" or "شامل")
            return DimensionClass.LumpSum;

        // Count / Discrete Units
        if (u is "no" or "nr" or "ea" or "each" or "pcs" or "piece" or "set" or "عدد" or "حبة" or "قطعة" or "طقم" or "نقطة" or "point" or "unit" or "وحدة")
            return DimensionClass.Count;

        // Time
        if (u is "month" or "mo" or "شهر" or "day" or "يوم" or "week" or "wk" or "اسبوع" or "hr" or "hour" or "ساعة")
            return DimensionClass.Time;

        // Power
        if (u is "kw" or "kva" or "hp" or "mw" or "حصان" or "كيلووات")
            return DimensionClass.Power;

        return DimensionClass.Unknown;
    }

    /// <summary>
    /// Evaluates compatibility score [0.0 - 1.0] between two engineering units.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateUnitCompatibility(string? unitA, string? unitB)
    {
        if (string.IsNullOrWhiteSpace(unitA) && string.IsNullOrWhiteSpace(unitB)) return 0.8;
        if (string.IsNullOrWhiteSpace(unitA) || string.IsNullOrWhiteSpace(unitB)) return 0.5;

        string a = unitA.Trim().ToLowerInvariant();
        string b = unitB.Trim().ToLowerInvariant();
        if (a == b) return 1.0;

        var classA = ClassifyUnit(a);
        var classB = ClassifyUnit(b);

        if (classA != DimensionClass.Unknown && classA == classB)
        {
            return 0.95; // Exact dimensional class match (e.g. m2 vs sqm, lm vs m)
        }

        // Count and LumpSum can often overlap in tender schedules (e.g. 1 Item vs 1 No)
        if ((classA == DimensionClass.Count && classB == DimensionClass.LumpSum) ||
            (classA == DimensionClass.LumpSum && classB == DimensionClass.Count))
        {
            return 0.85;
        }

        return 0.2; // Dimensional conflict
    }

    /// <summary>
    /// Computes proportional quantity similarity score [0.0 - 1.0] and detects 1:1, 1:1000 (ton/kg), or scale factors.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateQuantityProportion(decimal qtyA, decimal qtyB, out decimal scaleRatio)
    {
        scaleRatio = 1.0m;

        // Zero quantities (e.g. Rate-only / scope ignored)
        if (qtyA == 0m && qtyB == 0m) return 1.0;
        if (qtyA == 0m || qtyB == 0m) return 0.4;

        if (qtyA == qtyB)
        {
            return 1.0; // Identical quantity
        }

        decimal min = Math.Min(qtyA, qtyB);
        decimal max = Math.Max(qtyA, qtyB);
        decimal ratio = max / min;
        scaleRatio = ratio;

        // Standard engineering conversion multiplier: 1000x (e.g. Ton vs Kg, m3 vs Liter, Km vs M)
        if (Math.Abs(ratio - 1000.0m) < 1.0m)
        {
            return 0.95;
        }

        // Relative closeness within 1% (e.g. rounding differences)
        decimal relDiff = (max - min) / max;
        if (relDiff <= 0.01m) return 0.98;
        if (relDiff <= 0.05m) return 0.85;
        if (relDiff <= 0.15m) return 0.70;

        // Quantity proportion factor
        return Math.Max(0.1, 1.0 - (double)relDiff);
    }
}
