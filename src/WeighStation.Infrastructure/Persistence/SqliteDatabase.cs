using Microsoft.Data.Sqlite;

namespace WeighStation.Infrastructure.Persistence;

/// <summary>
/// Owns the database file and its schema. Every repository gets the same instance, so there is exactly
/// one place that knows how the tables look. Connections are short-lived: SQLite opens them in microseconds.
/// </summary>
public sealed class SqliteDatabase : WeighStation.Core.Abstractions.IDatabaseBackup
{
    private readonly string _connectionString;

    public SqliteDatabase(string path)
    {
        if (path != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        FilePath = path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        Initialize();
    }

    public string FilePath { get; }

    string WeighStation.Core.Abstractions.IDatabaseBackup.DatabasePath => FilePath;

    /// <summary>
    /// Uses SQLite's online backup API, which produces a consistent snapshot even while the application
    /// is writing - copying the file with File.Copy could catch it half-written.
    /// </summary>
    public async Task BackupAsync(string targetPath, CancellationToken ct = default)
    {
        await using var source = await OpenAsync(ct);
        await using var target = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = targetPath, Pooling = false }.ToString());
        await target.OpenAsync(ct);
        source.BackupDatabase(target);
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS weighings (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                time         INTEGER NOT NULL,
                plate        TEXT    NOT NULL,
                trailer      TEXT,
                driver       TEXT,
                counterparty TEXT,
                cargo        TEXT,
                kind         INTEGER NOT NULL,
                gross_kg     INTEGER,
                tare_kg      INTEGER,
                net_kg       INTEGER,
                status       INTEGER NOT NULL,
                operator     TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_weighings_plate ON weighings(plate, kind, id);
            CREATE INDEX IF NOT EXISTS ix_weighings_time ON weighings(time);

            CREATE TABLE IF NOT EXISTS weighing_history (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                weighing_id INTEGER NOT NULL REFERENCES weighings(id),
                time        INTEGER NOT NULL,
                user        TEXT    NOT NULL,
                text        TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_weighing ON weighing_history(weighing_id, time, id);

            CREATE TABLE IF NOT EXISTS vehicles (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                plate        TEXT NOT NULL UNIQUE,
                trailer      TEXT,
                driver       TEXT,
                company      TEXT,
                cargo        TEXT,
                tare_kg      INTEGER,
                max_mass_kg  INTEGER,
                note         TEXT
            );
            CREATE TABLE IF NOT EXISTS companies (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL,
                inn   TEXT,
                note  TEXT
            );
            CREATE TABLE IF NOT EXISTS cargos (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS drivers (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                full_name  TEXT NOT NULL,
                phone      TEXT
            );

            CREATE TABLE IF NOT EXISTS users (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                login           TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                full_name       TEXT    NOT NULL,
                role            INTEGER NOT NULL,
                is_active       INTEGER NOT NULL,
                must_change     INTEGER NOT NULL,
                password_hash   TEXT    NOT NULL,
                failed_attempts INTEGER NOT NULL,
                locked_until    INTEGER,
                created_at      INTEGER NOT NULL,
                last_login_at   INTEGER
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                time    INTEGER NOT NULL,
                user    TEXT    NOT NULL,
                action  TEXT    NOT NULL,
                details TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
