using Dapper;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Storage;

/// <summary>
/// Embedded local SQLite repository for offline snapshot persistence, revision tracking,
/// and historical rate benchmarking. Inherits from <see cref="BaseSqliteRepository"/>.
/// </summary>
public sealed class SqliteBoqRepository : BaseSqliteRepository
{
    public SqliteBoqRepository(string? databasePath = null) : base(databasePath)
    {
    }

    public override async Task InitializeDatabaseAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string schemaSql = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

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
        """;

        await connection.ExecuteAsync(schemaSql);
    }

    public override async Task SaveSnapshotAsync(ProjectSnapshot snapshot, IReadOnlyList<BoqItem> items, CancellationToken ct = default)
    {
        await ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            const string insertSnapshotSql = """
                INSERT INTO Snapshots (
                    ProjectCode, RevisionCode, SnapshotDate, 
                    TotalValueEgp, TotalValueUsd, TotalItemsCount, ContentHash
                ) VALUES (
                    @ProjectCode, @RevisionCode, @SnapshotDate, 
                    @TotalValueEgp, @TotalValueUsd, @TotalItemsCount, @ContentHash
                );
                SELECT last_insert_rowid();
            """;

            long revisionId = await connection.ExecuteScalarAsync<long>(insertSnapshotSql, new
            {
                snapshot.ProjectCode,
                snapshot.RevisionCode,
                SnapshotDate = snapshot.SnapshotDate.ToString("o"),
                snapshot.TotalValueEgp,
                snapshot.TotalValueUsd,
                snapshot.TotalItemsCount,
                snapshot.ContentHash
            }, transaction);

            const string insertItemSql = """
                INSERT OR REPLACE INTO SnapshotItems (
                    Id, RevisionId, BillNumber, SectionName, ItemCode,
                    Description, NormalizedDescription, Unit, Quantity,
                    UnitRate, TotalAmount, Currency, ItemType
                ) VALUES (
                    @Id, @RevisionId, @BillNumber, @SectionName, @ItemCode,
                    @Description, @NormalizedDescription, @Unit, @Quantity,
                    @UnitRate, @TotalAmount, @Currency, @ItemType
                );
            """;

            var itemParameters = items.Select(item => new
            {
                Id = $"{revisionId}_{item.Id}",
                RevisionId = revisionId,
                item.BillNumber,
                item.SectionName,
                item.ItemCode,
                item.Description,
                item.NormalizedDescription,
                item.Unit,
                item.Quantity,
                item.UnitRate,
                item.TotalAmount,
                item.Currency,
                ItemType = (int)item.Type
            });

            await connection.ExecuteAsync(insertItemSql, itemParameters, transaction);
        }, ct);
    }

    public override async Task<IReadOnlyList<ProjectSnapshot>> GetSnapshotsAsync(string projectCode, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string query = """
            SELECT 
                RevisionId, ProjectCode, RevisionCode, SnapshotDate,
                TotalValueEgp, TotalValueUsd, TotalItemsCount, ContentHash
            FROM Snapshots
            WHERE ProjectCode = @ProjectCode
            ORDER BY RevisionId DESC;
        """;

        var rows = await connection.QueryAsync(query, new { ProjectCode = projectCode });
        var list = new List<ProjectSnapshot>();

        foreach (var r in rows)
        {
            list.Add(new ProjectSnapshot
            {
                RevisionId = r.RevisionId,
                ProjectCode = r.ProjectCode,
                RevisionCode = r.RevisionCode,
                SnapshotDate = DateTime.Parse(r.SnapshotDate),
                TotalValueEgp = (decimal)r.TotalValueEgp,
                TotalValueUsd = (decimal)r.TotalValueUsd,
                TotalItemsCount = (int)r.TotalItemsCount,
                ContentHash = r.ContentHash ?? string.Empty
            });
        }

        return list;
    }

    public override async Task<IReadOnlyList<BoqItem>> FindHistoricalRatesAsync(string normalizedDescription, string unit, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string query = """
            SELECT 
                Id, BillNumber, SectionName, ItemCode,
                Description, NormalizedDescription, Unit, Quantity,
                UnitRate, TotalAmount, Currency, ItemType
            FROM SnapshotItems
            WHERE NormalizedDescription = @NormalizedDescription 
              AND Unit = @Unit
              AND UnitRate IS NOT NULL
            ORDER BY RevisionId DESC
            LIMIT 10;
        """;

        var rows = await connection.QueryAsync(query, new { NormalizedDescription = normalizedDescription, Unit = unit });
        var list = new List<BoqItem>();

        foreach (var r in rows)
        {
            list.Add(new BoqItem
            {
                Id = r.Id,
                BillNumber = r.BillNumber,
                SectionName = r.SectionName ?? string.Empty,
                ItemCode = r.ItemCode ?? string.Empty,
                Description = r.Description,
                NormalizedDescription = r.NormalizedDescription,
                Unit = r.Unit,
                Quantity = (decimal)r.Quantity,
                UnitRate = r.UnitRate != null ? (decimal)r.UnitRate : null,
                TotalAmount = r.TotalAmount != null ? (decimal)r.TotalAmount : null,
                Currency = r.Currency,
                Type = (BoqItemType)(int)r.ItemType
            });
        }

        return list;
    }
}
