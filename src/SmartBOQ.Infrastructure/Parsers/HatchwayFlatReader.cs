using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Parsers;

/// <summary>
/// Forward-only streaming reader for contractor flat tabular pricing schedules (e.g. DP3 - Hatchway.xlsx).
/// Inherits from <see cref="UniversalAdaptiveBoqReader"/> and adapts dynamically to any flat or tabular format.
/// </summary>
public sealed class HatchwayFlatReader : UniversalAdaptiveBoqReader
{
    // Inherits high-speed adaptive parsing from UniversalAdaptiveBoqReader
}
