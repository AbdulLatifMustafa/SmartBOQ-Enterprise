using Dapper;
using Fastenshtein;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Storage.Migrations;

namespace SmartBOQ.Infrastructure.Storage;

/// <summary>
/// Embedded local SQLite repository for offline snapshot persistence, revision tracking,
/// and smart multi-attribute historical rate benchmarking. Inherits from <see cref="BaseSqliteRepository"/>.
/// </summary>
public sealed class SqliteBoqRepository : BaseSqliteRepository
{
    private readonly DatabaseMigrator _migrator = new();

    public SqliteBoqRepository(string? databasePath = null) : base(databasePath)
    {
    }

    public override async Task InitializeDatabaseAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await _migrator.MigrateAsync(connection, ct);
    }

    public override async Task SaveSnapshotAsync(ProjectSnapshot snapshot, IReadOnlyList<BoqItem> items, CancellationToken ct = default)
    {
        await InitializeDatabaseAsync(ct);

        await ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            const string insertSnapshotSql = """
                INSERT INTO Snapshots (
                    ProjectCode, RevisionCode, InvoiceName, SourceFileName, TargetFileName, ExportFilePath,
                    SnapshotDate, TotalValueEgp, TotalValueUsd, TotalItemsCount, ContentHash
                ) VALUES (
                    @ProjectCode, @RevisionCode, @InvoiceName, @SourceFileName, @TargetFileName, @ExportFilePath,
                    @SnapshotDate, @TotalValueEgp, @TotalValueUsd, @TotalItemsCount, @ContentHash
                );
                SELECT last_insert_rowid();
            """;

            long revisionId = await connection.ExecuteScalarAsync<long>(insertSnapshotSql, new
            {
                snapshot.ProjectCode,
                snapshot.RevisionCode,
                InvoiceName = string.IsNullOrWhiteSpace(snapshot.InvoiceName) ? snapshot.ProjectCode : snapshot.InvoiceName,
                snapshot.SourceFileName,
                snapshot.TargetFileName,
                snapshot.ExportFilePath,
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
        await InitializeDatabaseAsync(ct);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string query = """
            SELECT 
                RevisionId, ProjectCode, RevisionCode,
                COALESCE(InvoiceName, '') AS InvoiceName,
                COALESCE(SourceFileName, '') AS SourceFileName,
                COALESCE(TargetFileName, '') AS TargetFileName,
                COALESCE(ExportFilePath, '') AS ExportFilePath,
                SnapshotDate, TotalValueEgp, TotalValueUsd, TotalItemsCount, ContentHash
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
                InvoiceName = r.InvoiceName,
                SourceFileName = r.SourceFileName,
                TargetFileName = r.TargetFileName,
                ExportFilePath = r.ExportFilePath,
                SnapshotDate = DateTime.TryParse((string)r.SnapshotDate, out DateTime dt) ? dt : DateTime.UtcNow,
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

    /// <summary>
    /// Intelligently searches historical priced items across invoice name, project code, source/target file names,
    /// item code, description, and bill numbers. Supports smart ranking and fuzzy fallback.
    /// </summary>
    public override async Task<IReadOnlyList<HistoricalRateItem>> SearchHistoricalRatesAsync(
        string? searchTerm = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        await InitializeDatabaseAsync(ct);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        string trimmed = (searchTerm ?? string.Empty).Trim();

        // 1. If query is empty, return the most recent priced historical items
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            const string recentSql = """
                SELECT 
                    s.RevisionId,
                    COALESCE(NULLIF(s.InvoiceName, ''), s.ProjectCode) AS InvoiceName,
                    s.ProjectCode,
                    COALESCE(s.SourceFileName, '') AS SourceFileName,
                    COALESCE(s.TargetFileName, '') AS TargetFileName,
                    s.SnapshotDate,
                    i.BillNumber,
                    i.SectionName,
                    i.ItemCode,
                    i.Description,
                    i.Unit,
                    i.Quantity,
                    i.UnitRate,
                    i.TotalAmount,
                    i.Currency
                FROM SnapshotItems i
                JOIN Snapshots s ON i.RevisionId = s.RevisionId
                WHERE i.UnitRate IS NOT NULL
                ORDER BY s.RevisionId DESC, i.Id ASC
                LIMIT @Limit;
            """;

            var recentRows = await connection.QueryAsync(recentSql, new { Limit = limit });
            return MapHistoricalRows(recentRows);
        }

        // 2. Perform intelligent multi-attribute SQL search
        string likeTerm = $"%{trimmed}%";
        const string searchSql = """
            SELECT 
                s.RevisionId,
                COALESCE(NULLIF(s.InvoiceName, ''), s.ProjectCode) AS InvoiceName,
                s.ProjectCode,
                COALESCE(s.SourceFileName, '') AS SourceFileName,
                COALESCE(s.TargetFileName, '') AS TargetFileName,
                COALESCE(s.ExportFilePath, '') AS ExportFilePath,
                s.SnapshotDate,
                i.BillNumber,
                i.SectionName,
                i.ItemCode,
                i.Description,
                i.NormalizedDescription,
                i.Unit,
                i.Quantity,
                i.UnitRate,
                i.TotalAmount,
                i.Currency
            FROM SnapshotItems i
            JOIN Snapshots s ON i.RevisionId = s.RevisionId
            WHERE i.UnitRate IS NOT NULL
              AND (
                  i.Description LIKE @LikeTerm
                  OR i.NormalizedDescription LIKE @LikeTerm
                  OR i.ItemCode LIKE @LikeTerm
                  OR s.InvoiceName LIKE @LikeTerm
                  OR s.ProjectCode LIKE @LikeTerm
                  OR s.SourceFileName LIKE @LikeTerm
                  OR s.TargetFileName LIKE @LikeTerm
                  OR i.BillNumber LIKE @LikeTerm
              )
            ORDER BY s.RevisionId DESC
            LIMIT @Limit;
        """;

        var rows = (await connection.QueryAsync(searchSql, new { LikeTerm = likeTerm, Limit = limit })).ToList();

        // 3. Smart Fuzzy Fallback: if few or no direct matches, tokenize and search for fuzzy matches
        if (rows.Count < 10 && trimmed.Length >= 3)
        {
            var tokens = trimmed.Split(new[] { ' ', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0)
            {
                // Query a candidate pool from the most recent snapshots
                const string candidateSql = """
                    SELECT 
                        s.RevisionId,
                        COALESCE(NULLIF(s.InvoiceName, ''), s.ProjectCode) AS InvoiceName,
                        s.ProjectCode,
                        COALESCE(s.SourceFileName, '') AS SourceFileName,
                        COALESCE(s.TargetFileName, '') AS TargetFileName,
                        COALESCE(s.ExportFilePath, '') AS ExportFilePath,
                        s.SnapshotDate,
                        i.BillNumber,
                        i.SectionName,
                        i.ItemCode,
                        i.Description,
                        i.NormalizedDescription,
                        i.Unit,
                        i.Quantity,
                        i.UnitRate,
                        i.TotalAmount,
                        i.Currency
                    FROM SnapshotItems i
                    JOIN Snapshots s ON i.RevisionId = s.RevisionId
                    WHERE i.UnitRate IS NOT NULL
                    ORDER BY s.RevisionId DESC
                    LIMIT 2000;
                """;

                var candidates = await connection.QueryAsync(candidateSql);
                var existingKeys = new HashSet<string>(rows.Select(r => $"{r.RevisionId}_{r.ItemCode}_{r.Description}"));

                var fuzzyMatches = new List<(dynamic row, double score)>();
                string lowerQuery = trimmed.ToLowerInvariant();

                foreach (var c in candidates)
                {
                    string key = $"{c.RevisionId}_{c.ItemCode}_{c.Description}";
                    if (existingKeys.Contains(key)) continue;

                    string desc = ((string)(c.Description ?? string.Empty)).ToLowerInvariant();
                    string norm = ((string)(c.NormalizedDescription ?? string.Empty)).ToLowerInvariant();
                    string inv = ((string)(c.InvoiceName ?? string.Empty)).ToLowerInvariant();
                    string src = ((string)(c.SourceFileName ?? string.Empty)).ToLowerInvariant();

                    double bestTokenScore = 0.0;
                    foreach (var tok in tokens)
                    {
                        string ltok = tok.ToLowerInvariant();
                        if (desc.Contains(ltok) || norm.Contains(ltok) || inv.Contains(ltok) || src.Contains(ltok))
                        {
                            bestTokenScore = Math.Max(bestTokenScore, 0.75);
                        }
                        else
                        {
                            // Levenshtein distance on words
                            var descWords = desc.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var w in descWords)
                            {
                                if (Math.Abs(w.Length - ltok.Length) <= 2)
                                {
                                    int dist = Levenshtein.Distance(w, ltok);
                                    if (dist <= 2)
                                    {
                                        double sim = 1.0 - ((double)dist / Math.Max(w.Length, ltok.Length));
                                        if (sim > bestTokenScore) bestTokenScore = sim;
                                    }
                                }
                            }
                        }
                    }

                    if (bestTokenScore >= 0.60)
                    {
                        fuzzyMatches.Add((c, bestTokenScore));
                    }
                }

                // Append top fuzzy matches
                foreach (var (cand, _) in fuzzyMatches.OrderByDescending(x => x.score).Take(limit - rows.Count))
                {
                    rows.Add(cand);
                }
            }
        }

        return MapHistoricalRows(rows);
    }

    private static IReadOnlyList<HistoricalRateItem> MapHistoricalRows(IEnumerable<dynamic> rows)
    {
        var list = new List<HistoricalRateItem>();
        foreach (var r in rows)
        {
            list.Add(new HistoricalRateItem
            {
                RevisionId = r.RevisionId,
                InvoiceName = r.InvoiceName ?? string.Empty,
                ProjectCode = r.ProjectCode ?? string.Empty,
                SourceFileName = r.SourceFileName ?? string.Empty,
                TargetFileName = r.TargetFileName ?? string.Empty,
                ExportFilePath = r.ExportFilePath ?? string.Empty,
                SnapshotDate = DateTime.TryParse((string)r.SnapshotDate, out DateTime dt) ? dt : DateTime.UtcNow,
                BillNumber = r.BillNumber ?? string.Empty,
                SectionName = r.SectionName ?? string.Empty,
                ItemCode = r.ItemCode ?? string.Empty,
                Description = r.Description ?? string.Empty,
                Unit = r.Unit ?? string.Empty,
                Quantity = (decimal)r.Quantity,
                UnitRate = r.UnitRate != null ? (decimal)r.UnitRate : null,
                TotalAmount = r.TotalAmount != null ? (decimal)r.TotalAmount : null,
                Currency = r.Currency ?? "EGP"
            });
        }
        return list;
    }

    #region Mapping Presets & Audit Trail

    public override async Task SaveMappingPresetAsync(MappingPreset preset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        await InitializeDatabaseAsync(ct);

        const string sql = """
            INSERT INTO MappingPresets (
                PresetName, ContractorName, SourceRateCol, TargetRateCol,
                SourceDescCol, TargetDescCol, SourceCodeCol, TargetCodeCol,
                SourceQtyCol, TargetQtyCol, SourceUnitCol, TargetUnitCol, CreatedAt
            ) VALUES (
                @PresetName, @ContractorName, @SourceRateCol, @TargetRateCol,
                @SourceDescCol, @TargetDescCol, @SourceCodeCol, @TargetCodeCol,
                @SourceQtyCol, @TargetQtyCol, @SourceUnitCol, @TargetUnitCol, @CreatedAt
            )
            ON CONFLICT(PresetName) DO UPDATE SET
                ContractorName = excluded.ContractorName,
                SourceRateCol = excluded.SourceRateCol,
                TargetRateCol = excluded.TargetRateCol,
                SourceDescCol = excluded.SourceDescCol,
                TargetDescCol = excluded.TargetDescCol,
                SourceCodeCol = excluded.SourceCodeCol,
                TargetCodeCol = excluded.TargetCodeCol,
                SourceQtyCol = excluded.SourceQtyCol,
                TargetQtyCol = excluded.TargetQtyCol,
                SourceUnitCol = excluded.SourceUnitCol,
                TargetUnitCol = excluded.TargetUnitCol,
                CreatedAt = excluded.CreatedAt;
        """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            preset.PresetName,
            preset.ContractorName,
            preset.SourceRateCol,
            preset.TargetRateCol,
            preset.SourceDescCol,
            preset.TargetDescCol,
            preset.SourceCodeCol,
            preset.TargetCodeCol,
            preset.SourceQtyCol,
            preset.TargetQtyCol,
            preset.SourceUnitCol,
            preset.TargetUnitCol,
            CreatedAt = preset.CreatedAt.ToString("o")
        }, cancellationToken: ct));
    }

    public override async Task<IReadOnlyList<MappingPreset>> GetMappingPresetsAsync(CancellationToken ct = default)
    {
        await InitializeDatabaseAsync(ct);

        const string sql = "SELECT * FROM MappingPresets ORDER BY CreatedAt DESC;";

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));

        var list = new List<MappingPreset>();
        foreach (var r in rows)
        {
            list.Add(new MappingPreset
            {
                PresetName = r.PresetName,
                ContractorName = r.ContractorName ?? string.Empty,
                SourceRateCol = (int)r.SourceRateCol,
                TargetRateCol = (int)r.TargetRateCol,
                SourceDescCol = (int)r.SourceDescCol,
                TargetDescCol = (int)r.TargetDescCol,
                SourceCodeCol = (int)r.SourceCodeCol,
                TargetCodeCol = (int)r.TargetCodeCol,
                SourceQtyCol = (int)r.SourceQtyCol,
                TargetQtyCol = (int)r.TargetQtyCol,
                SourceUnitCol = (int)r.SourceUnitCol,
                TargetUnitCol = (int)r.TargetUnitCol,
                CreatedAt = DateTime.TryParse((string)r.CreatedAt, out DateTime dt) ? dt : DateTime.UtcNow
            });
        }
        return list;
    }

    public override async Task DeleteMappingPresetAsync(string presetName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        await InitializeDatabaseAsync(ct);

        const string sql = "DELETE FROM MappingPresets WHERE PresetName = @PresetName;";

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { PresetName = presetName }, cancellationToken: ct));
    }

    public override async Task RecordAuditLogAsync(ItemAuditLog log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        await InitializeDatabaseAsync(ct);

        const string sql = """
            INSERT INTO ItemAuditTrail (
                ItemId, BillNumber, ItemCode, Description, OldRate, NewRate, Action, Reason, Engineer, Timestamp
            ) VALUES (
                @ItemId, @BillNumber, @ItemCode, @Description, @OldRate, @NewRate, @Action, @Reason, @Engineer, @Timestamp
            );
        """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            log.ItemId,
            log.BillNumber,
            log.ItemCode,
            log.Description,
            log.OldRate,
            log.NewRate,
            log.Action,
            log.Reason,
            log.Engineer,
            Timestamp = log.Timestamp.ToString("o")
        }, cancellationToken: ct));
    }

    public override async Task<IReadOnlyList<ItemAuditLog>> GetAuditLogsForItemAsync(string itemId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await InitializeDatabaseAsync(ct);

        const string sql = "SELECT * FROM ItemAuditTrail WHERE ItemId = @ItemId ORDER BY Timestamp DESC;";

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, new { ItemId = itemId }, cancellationToken: ct));

        var list = new List<ItemAuditLog>();
        foreach (var r in rows)
        {
            list.Add(new ItemAuditLog
            {
                AuditId = (long)r.AuditId,
                ItemId = r.ItemId,
                BillNumber = r.BillNumber,
                ItemCode = r.ItemCode,
                Description = r.Description,
                OldRate = r.OldRate != null ? (decimal)r.OldRate : null,
                NewRate = r.NewRate != null ? (decimal)r.NewRate : null,
                Action = r.Action,
                Reason = r.Reason,
                Engineer = r.Engineer,
                Timestamp = DateTime.TryParse((string)r.Timestamp, out DateTime dt) ? dt : DateTime.UtcNow
            });
        }
        return list;
    }

    #endregion
}
