using System.Data.Common;
using Dapper;

namespace SmartBOQ.Infrastructure.Storage.Migrations;

/// <summary>
/// Robust, transactional SQLite migration coordinator.
/// Uses SQLite's internal 'PRAGMA user_version' to track database schema versioning
/// and sequentially applies pending migrations with atomic rollbacks on failure.
/// </summary>
public sealed class DatabaseMigrator
{
    private readonly IReadOnlyList<IDbMigration> _migrations;

    public DatabaseMigrator(IEnumerable<IDbMigration>? customMigrations = null)
    {
        _migrations = customMigrations?.OrderBy(m => m.Version).ToList() ??
        [
            new Migration_001_InitialSchema(),
            new Migration_002_AddFlexibleColumns(),
            new Migration_003_AddPresetsAndAudit()
        ];
    }

    /// <summary>
    /// Checks current database version and applies all pending migrations.
    /// </summary>
    public async Task MigrateAsync(DbConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // 1. Optimize SQLite engine parameters
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA synchronous = NORMAL;", cancellationToken: ct));

        // 2. Read current schema version from SQLite
        int currentVersion = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("PRAGMA user_version;", cancellationToken: ct));

        // 3. Apply pending migrations sequentially
        foreach (var migration in _migrations.Where(m => m.Version > currentVersion).OrderBy(m => m.Version))
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await migration.UpAsync(connection, transaction, ct);

                // Update database user_version atomically inside transaction
                string setVersionSql = $"PRAGMA user_version = {migration.Version};";
                await connection.ExecuteAsync(new CommandDefinition(setVersionSql, transaction: transaction, cancellationToken: ct));

                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }
    }
}
