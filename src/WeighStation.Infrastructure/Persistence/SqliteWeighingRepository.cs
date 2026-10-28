using Microsoft.Data.Sqlite;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Infrastructure.Persistence;

/// <summary>The journal. Also serves as the tare reference: the latest tare weighing per plate.</summary>
public sealed class SqliteWeighingRepository(SqliteDatabase db) : IWeighingRepository, ITareProvider
{
    public async Task<Weighing> AddAsync(Weighing w, IReadOnlyList<AuditEntry> history, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // One transaction: a weighing without its history (or the other way round) must never be visible.
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO weighings
                (time, plate, trailer, driver, counterparty, cargo, kind, gross_kg, tare_kg, net_kg, status, operator)
            VALUES
                ($time, $plate, $trailer, $driver, $counterparty, $cargo, $kind, $gross, $tare, $net, $status, $operator);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$time", w.Time.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$plate", PlateNumber.Normalize(w.Plate));
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

        foreach (var entry in history)
        {
            await InsertHistoryAsync(conn, tx, id, entry, ct);
        }

        await tx.CommitAsync(ct);
        return w with { Id = id, Plate = PlateNumber.Normalize(w.Plate) };
    }

    public async Task<IReadOnlyList<Weighing>> GetRecentAsync(int count, CancellationToken ct = default) =>
        await QueryAsync(new WeighingQuery { Limit = count }, ct);

    public async Task<IReadOnlyList<Weighing>> QueryAsync(WeighingQuery q, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (q.From is { } from)
        {
            where.Add("time >= $from");
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        }

        if (q.To is { } to)
        {
            where.Add("time < $to");
            cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        }

        var plate = PlateNumber.Normalize(q.Plate);
        if (plate.Length > 0)
        {
            where.Add("instr(plate, $plate) > 0");
            cmd.Parameters.AddWithValue("$plate", plate);
        }

        if (q.Status is { } status)
        {
            where.Add("status = $status");
            cmd.Parameters.AddWithValue("$status", (int)status);
        }

        // SQLite can only fold case for ASCII, so the company filter runs in memory (after the cheap SQL filters).
        var companyFilter = string.IsNullOrWhiteSpace(q.Company) ? null : q.Company.Trim();
        cmd.CommandText = "SELECT * FROM weighings"
                          + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                          + " ORDER BY time DESC, id DESC"
                          + (companyFilter is null ? " LIMIT $limit" : "");
        if (companyFilter is null)
        {
            cmd.Parameters.AddWithValue("$limit", q.Limit);
        }

        var result = new List<Weighing>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var w = ReadWeighing(r);
            if (companyFilter is null
                || (w.Counterparty?.Contains(companyFilter, StringComparison.CurrentCultureIgnoreCase) ?? false))
            {
                result.Add(w);
            }
        }

        return companyFilter is null ? result : result.Take(q.Limit).ToList();
    }

    public async Task<Weighing?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM weighings WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadWeighing(r) : null;
    }

    public async Task UpdateDescriptionAsync(Weighing edited, AuditEntry entry, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE weighings
            SET plate = $plate, trailer = $trailer, driver = $driver, counterparty = $company, cargo = $cargo,
                status = CASE WHEN status = $cancelled THEN status ELSE $manual END
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", edited.Id);
        cmd.Parameters.AddWithValue("$plate", PlateNumber.Normalize(edited.Plate));
        cmd.Parameters.AddWithValue("$trailer", (object?)edited.Trailer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$driver", (object?)edited.Driver ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$company", (object?)edited.Counterparty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cargo", (object?)edited.Cargo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cancelled", (int)WeighingStatus.Cancelled);
        cmd.Parameters.AddWithValue("$manual", (int)WeighingStatus.ManualEdit);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            throw new KeyNotFoundException($"Weighing {edited.Id} not found.");
        }

        await InsertHistoryAsync(conn, tx, edited.Id, entry, ct);
        await tx.CommitAsync(ct);
    }

    public async Task CancelAsync(long id, AuditEntry entry, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE weighings SET status = $cancelled WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$cancelled", (int)WeighingStatus.Cancelled);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            throw new KeyNotFoundException($"Weighing {id} not found.");
        }

        await InsertHistoryAsync(conn, tx, id, entry, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetHistoryAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT time, user, text FROM weighing_history WHERE weighing_id = $id ORDER BY time, id";
        cmd.Parameters.AddWithValue("$id", id);
        var result = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new AuditEntry(
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).ToLocalTime(), r.GetString(1), r.GetString(2)));
        }

        return result;
    }

    public async Task<int?> FindTareKgAsync(string plate, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT tare_kg FROM weighings
            WHERE plate = $plate AND kind = $kind AND tare_kg IS NOT NULL AND status <> $cancelled
            ORDER BY id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$plate", PlateNumber.Normalize(plate));
        cmd.Parameters.AddWithValue("$kind", (int)WeighingKind.Tare);
        cmd.Parameters.AddWithValue("$cancelled", (int)WeighingStatus.Cancelled);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async Task InsertHistoryAsync(
        SqliteConnection conn, SqliteTransaction tx, long weighingId, AuditEntry entry, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO weighing_history (weighing_id, time, user, text) VALUES ($w, $t, $u, $x)";
        cmd.Parameters.AddWithValue("$w", weighingId);
        cmd.Parameters.AddWithValue("$t", entry.Time.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$u", entry.User);
        cmd.Parameters.AddWithValue("$x", entry.Text);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Weighing ReadWeighing(SqliteDataReader r) => new()
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
    };

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
}
