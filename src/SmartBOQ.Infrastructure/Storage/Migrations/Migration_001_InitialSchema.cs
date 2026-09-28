using System.Data.Common;
using Dapper;

namespace SmartBOQ.Infrastructure.Storage.Migrations;

/// <summary>
/// Initial database migration establishing core snapshot and item persistence tables with indexes.
/// </summary>
public sealed class Migration_001_InitialSchema : IDbMigration
{
    public int Version => 1;
    public string Description => "Create base Snapshots and SnapshotItems tables with performance indexes";

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS Snapshots (
                RevisionId INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectCode TEXT NOT NULL,
                RevisionCode TEXT NOT NULL,
                SnapshotDate TEXT NOT NULL,
                TotalValueEgp REAL NOT NULL,
                TotalValueUsd REAL NOT NULL,
                TotalItemsCount INTEGER NOT NULL,
                ContentHash TEXT
            );

            CREATE TABLE IF NOT EXISTS SnapshotItems (
                Id TEXT PRIMARY KEY,
                RevisionId INTEGER NOT NULL,
                BillNumber TEXT NOT NULL,
                SectionName TEXT,
                ItemCode TEXT,
                Description TEXT NOT NULL,
                NormalizedDescription TEXT NOT NULL,
                Unit TEXT NOT NULL,
                Quantity REAL NOT NULL,
                UnitRate REAL,
                TotalAmount REAL,
                Currency TEXT NOT NULL,
                ItemType INTEGER NOT NULL,
                FOREIGN KEY (RevisionId) REFERENCES Snapshots(RevisionId) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_SnapshotItems_NormDesc_Unit 
            ON SnapshotItems(NormalizedDescription, Unit);

            CREATE INDEX IF NOT EXISTS IX_Snapshots_ProjectCode 
            ON Snapshots(ProjectCode);

            CREATE INDEX IF NOT EXISTS IX_SnapshotItems_Revision_Rate
            ON SnapshotItems(RevisionId, UnitRate);
        """;

        await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: ct));
    }
}
