namespace SmartBOQ.Domain.Models;

/// <summary>
/// Audit trail entry tracking manual rate modifications, approvals, and contract overrides.
/// </summary>
public sealed record ItemAuditLog
{
    public long AuditId { get; init; }
    public required string ItemId { get; init; }
    public required string BillNumber { get; init; }
    public string? ItemCode { get; init; }
    public string? Description { get; init; }
    public decimal? OldRate { get; init; }
    public decimal? NewRate { get; init; }
    public required string Action { get; init; }
    public string? Reason { get; init; }
    public string? Engineer { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
