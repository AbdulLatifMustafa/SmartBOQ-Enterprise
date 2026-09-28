using System.Data.Common;
using Dapper;

namespace SmartBOQ.Infrastructure.Storage.Migrations;

/// <summary>
/// Migration adding flexible invoice, workbook source, target, and export paths to Snapshots.
/// </summary>
public sealed class Migration_002_AddFlexibleColumns : IDbMigration
{
    public int Version => 2;
    public string Description => "Add InvoiceName, SourceFileName, TargetFileName, and ExportFilePath columns to Snapshots";

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default)
    {
        var existingCols = (await connection.QueryAsync<string>(
            new CommandDefinition("SELECT name FROM pragma_table_info('Snapshots');", transaction: transaction, cancellationToken: ct)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingCols.Contains("InvoiceName"))
        {
            await connection.ExecuteAsync(new CommandDefinition("ALTER TABLE Snapshots ADD COLUMN InvoiceName TEXT;", transaction: transaction, cancellationToken: ct));
        }
        if (!existingCols.Contains("SourceFileName"))
        {
            await connection.ExecuteAsync(new CommandDefinition("ALTER TABLE Snapshots ADD COLUMN SourceFileName TEXT;", transaction: transaction, cancellationToken: ct));
        }
        if (!existingCols.Contains("TargetFileName"))
        {
            await connection.ExecuteAsync(new CommandDefinition("ALTER TABLE Snapshots ADD COLUMN TargetFileName TEXT;", transaction: transaction, cancellationToken: ct));
        }
        if (!existingCols.Contains("ExportFilePath"))
        {
            await connection.ExecuteAsync(new CommandDefinition("ALTER TABLE Snapshots ADD COLUMN ExportFilePath TEXT;", transaction: transaction, cancellationToken: ct));
        }
    }
}
