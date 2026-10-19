using System.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Data;

public static class DatabaseInitializer
{
    public const string BaselineMigration = "20261016150630_InitialCreate";
    // Схема MVP без истории бывает у баз, созданных через EnsureCreated (тесты, ранние сборки).
    public const string MvpMigration = "20261016152747_Mvp";

    public static async Task InitializeAsync(AppDbContext db, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (await TableExistsAsync(db, "Teachers", ct) && !await TableExistsAsync(db, "__EFMigrationsHistory", ct))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)", ct);
                foreach (var migration in await AppliedWithoutHistoryAsync(db, ct))
                    await db.Database.ExecuteSqlAsync(
                        $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migration}, '10.0.12')", ct);
                await transaction.CommitAsync(ct);
            }
        }
        finally { if (close) await db.Database.CloseConnectionAsync(); }
        await db.Database.MigrateAsync(ct);
    }

    public static void Initialize(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) db.Database.OpenConnection();
        try
        {
            if (TableExists(db, "Teachers") && !TableExists(db, "__EFMigrationsHistory"))
            {
                using var transaction = db.Database.BeginTransaction();
                db.Database.ExecuteSqlRaw(
                    "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
                foreach (var migration in AppliedWithoutHistoryAsync(db, CancellationToken.None).GetAwaiter().GetResult())
                    db.Database.ExecuteSql(
                        $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migration}, '10.0.12')");
                transaction.Commit();
            }
        }
        finally { if (close) db.Database.CloseConnection(); }
        db.Database.Migrate();
    }

    /// База без истории: таблицы MVP означают схему MVP, иначе это схема первой версии.
    private static async Task<IReadOnlyList<string>> AppliedWithoutHistoryAsync(AppDbContext db, CancellationToken ct) =>
        await TableExistsAsync(db, "FaceSubmissions", ct) ? [BaselineMigration, MvpMigration] : [BaselineMigration];

    private static async Task<bool> TableExistsAsync(AppDbContext db, string name, CancellationToken ct)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$name", name));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private static bool TableExists(AppDbContext db, string name)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$name", name));
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }
}
