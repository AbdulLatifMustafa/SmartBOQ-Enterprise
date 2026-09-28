namespace SmartBOQ.Domain.Models;

/// <summary>
/// Persistent contractor column routing preset.
/// Allows saving custom channel configurations and reloading them automatically.
/// </summary>
public sealed record MappingPreset
{
    public required string PresetName { get; init; }
    public string ContractorName { get; init; } = string.Empty;
    public int SourceRateCol { get; init; } = -1;
    public int TargetRateCol { get; init; } = -1;
    public int SourceDescCol { get; init; } = -1;
    public int TargetDescCol { get; init; } = -1;
    public int SourceCodeCol { get; init; } = -1;
    public int TargetCodeCol { get; init; } = -1;
    public int SourceQtyCol { get; init; } = -1;
    public int TargetQtyCol { get; init; } = -1;
    public int SourceUnitCol { get; init; } = -1;
    public int TargetUnitCol { get; init; } = -1;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
