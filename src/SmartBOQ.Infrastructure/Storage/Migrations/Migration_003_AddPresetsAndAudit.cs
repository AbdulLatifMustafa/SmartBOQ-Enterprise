using System.Data.Common;
using Dapper;

namespace SmartBOQ.Infrastructure.Storage.Migrations;

/// <summary>
/// Migration creating persistent mapping presets and audit trail tracking tables.
/// </summary>
public sealed class Migration_003_AddPresetsAndAudit : IDbMigration
{
    public int Version => 3;
    public string Description => "Create MappingPresets and ItemAuditTrail tables for configuration and compliance";

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS MappingPresets (
                PresetName TEXT PRIMARY KEY,
                ContractorName TEXT NOT NULL,
                SourceRateCol INTEGER NOT NULL,
                TargetRateCol INTEGER NOT NULL,
                SourceDescCol INTEGER NOT NULL,
                TargetDescCol INTEGER NOT NULL,
                SourceCodeCol INTEGER NOT NULL,
                TargetCodeCol INTEGER NOT NULL,
                SourceQtyCol INTEGER NOT NULL,
                TargetQtyCol INTEGER NOT NULL,
                SourceUnitCol INTEGER NOT NULL,
                TargetUnitCol INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ItemAuditTrail (
                AuditId INTEGER PRIMARY KEY AUTOINCREMENT,
                ItemId TEXT NOT NULL,
                BillNumber TEXT NOT NULL,
                ItemCode TEXT,
                Description TEXT,
                OldRate REAL,
                NewRate REAL,
                Action TEXT NOT NULL,
                Reason TEXT,
                Engineer TEXT,
                Timestamp TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_AuditTrail_ItemId
            ON ItemAuditTrail(ItemId);

            CREATE INDEX IF NOT EXISTS IX_AuditTrail_Timestamp
            ON ItemAuditTrail(Timestamp);
        """;

        await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: ct));
    }
}
