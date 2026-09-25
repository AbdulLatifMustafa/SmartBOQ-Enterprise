using Microsoft.Data.Sqlite;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Storage;

/// <summary>
/// Abstract base class providing SQLite connection management, transaction wrapping,
/// and connection pooling for local offline repositories.
/// </summary>
public abstract class BaseSqliteRepository : ISqliteRepository
{
    protected string ConnectionString { get; }

    protected BaseSqliteRepository(string? databasePath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartBOQ");
            Directory.CreateDirectory(appData);
            databasePath = Path.Combine(appData, "smartboq.db");
        }
        else
        {
            string? dir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    protected SqliteConnection CreateConnection() => new(ConnectionString);

    protected async Task ExecuteInTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> action, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await action(connection, (SqliteTransaction)transaction);
        await transaction.CommitAsync(ct);
    }

    protected async Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        T result = await action(connection, (SqliteTransaction)transaction);
        await transaction.CommitAsync(ct);
        return result;
    }

    public abstract Task InitializeDatabaseAsync(CancellationToken ct = default);
    public abstract Task SaveSnapshotAsync(ProjectSnapshot snapshot, IReadOnlyList<BoqItem> items, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<ProjectSnapshot>> GetSnapshotsAsync(string projectCode, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<BoqItem>> FindHistoricalRatesAsync(string normalizedDescription, string unit, CancellationToken ct = default);
}
