using System.Data.Common;

namespace SmartBOQ.Infrastructure.Storage.Migrations;

/// <summary>
/// Contract for an incremental, idempotent database migration step.
/// </summary>
public interface IDbMigration
{
    /// <summary>
    /// Sequential target database version number (1, 2, 3...).
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Human-readable explanation of schema mutations applied in this step.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Executes schema creation or alteration inside an active database transaction.
    /// </summary>
    Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default);
}
