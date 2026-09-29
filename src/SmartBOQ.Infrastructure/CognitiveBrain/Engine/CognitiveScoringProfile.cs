namespace SmartBOQ.Infrastructure.CognitiveBrain.Engine;

/// <summary>
/// Adaptive scoring profile that dynamically adjusts evaluation weights
/// based on the structural quality of the input files.
/// </summary>
public sealed class CognitiveScoringProfile
{
    public double WeightCode { get; set; } = 0.25;
    public double WeightDescription { get; set; } = 0.35;
    public double WeightUnit { get; set; } = 0.15;
    public double WeightQuantity { get; set; } = 0.15;
    public double WeightPriceHarmony { get; set; } = 0.10;

    /// <summary>
    /// Calibrates weights dynamically based on whether item codes and quantities are available in the schedule.
    /// </summary>
    public static CognitiveScoringProfile CreateAdaptive(bool hasCodes, bool hasDistinctQuantities)
    {
        var profile = new CognitiveScoringProfile();

        if (!hasCodes && hasDistinctQuantities)
        {
            // No codes: transfer code weight into description and quantity
            profile.WeightCode = 0.05;
            profile.WeightDescription = 0.50;
            profile.WeightQuantity = 0.25;
            profile.WeightUnit = 0.15;
            profile.WeightPriceHarmony = 0.05;
        }
        else if (hasCodes && !hasDistinctQuantities)
        {
            // All quantities 1 (e.g. Lump sum schedules): transfer quantity weight to code and description
            profile.WeightCode = 0.35;
            profile.WeightDescription = 0.40;
            profile.WeightUnit = 0.15;
            profile.WeightQuantity = 0.05;
            profile.WeightPriceHarmony = 0.05;
        }

        return profile;
    }
}
