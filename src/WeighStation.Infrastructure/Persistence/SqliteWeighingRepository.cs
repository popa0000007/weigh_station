using Microsoft.Data.Sqlite;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Infrastructure.Persistence;

/// <summary>Local SQLite journal. Also serves as the tare reference: the latest tare weighing per plate.</summary>
public sealed class SqliteWeighingRepository : IWeighingRepository, ITareProvider
{
    private readonly string _connectionString;

    public SqliteWeighingRepository(string databasePath)
    {
        if (databasePath != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        Initialize();
    }

    public async Task<Weighing> AddAsync(Weighing w, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO weighings
                (time, plate, trailer, driver, counterparty, cargo, kind, gross_kg, tare_kg, net_kg, status, operator)
            VALUES
                ($time, $plate, $trailer, $driver, $counterparty, $cargo, $kind, $gross, $tare, $net, $status, $operator);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$time", w.Time.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$plate", w.Plate);
        cmd.Parameters.AddWithValue("$trailer", (object?)w.Trailer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$driver", (object?)w.Driver ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$counterparty", (object?)w.Counterparty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cargo", (object?)w.Cargo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", (int)w.Kind);
        cmd.Parameters.AddWithValue("$gross", (object?)w.GrossKg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tare", (object?)w.TareKg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$net", (object?)w.NetKg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (int)w.Status);
        cmd.Parameters.AddWithValue("$operator", (object?)w.Operator ?? DBNull.Value);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return w with { Id = id };
    }

    public async Task<IReadOnlyList<Weighing>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM weighings ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        var result = new List<Weighing>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new Weighing
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                Time = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("time"))).ToLocalTime(),
                Plate = r.GetString(r.GetOrdinal("plate")),
                Trailer = Str(r, "trailer"),
                Driver = Str(r, "driver"),
                Counterparty = Str(r, "counterparty"),
                Cargo = Str(r, "cargo"),
                Kind = (WeighingKind)r.GetInt32(r.GetOrdinal("kind")),
                GrossKg = Int(r, "gross_kg"),
                TareKg = Int(r, "tare_kg"),
                NetKg = Int(r, "net_kg"),
                Status = (WeighingStatus)r.GetInt32(r.GetOrdinal("status")),
                Operator = Str(r, "operator"),
            });
        }

        return result;
    }

    public async Task<int?> FindTareKgAsync(string plate, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT tare_kg FROM weighings
            WHERE plate = $plate AND kind = $kind AND tare_kg IS NOT NULL AND status <> $cancelled
            ORDER BY id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$plate", plate);
        cmd.Parameters.AddWithValue("$kind", (int)WeighingKind.Tare);
        cmd.Parameters.AddWithValue("$cancelled", (int)WeighingStatus.Cancelled);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static string? Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static int? Int(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
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
            """;
        cmd.ExecuteNonQuery();
    }
}
