using Microsoft.Data.Sqlite;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Infrastructure.Persistence;

public sealed class SqliteUserStore(SqliteDatabase db) : IUserStore
{
    private const string Columns =
        "id, login, full_name, role, is_active, must_change, password_hash, failed_attempts, locked_until, created_at, last_login_at";

    public async Task<IReadOnlyList<StoredUser>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM users ORDER BY id";
        var list = new List<StoredUser>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public Task<StoredUser?> GetAsync(long id, CancellationToken ct = default) => QuerySingleAsync("id = $v", id, ct);

    public Task<StoredUser?> FindByLoginAsync(string login, CancellationToken ct = default) =>
        QuerySingleAsync("login = $v COLLATE NOCASE", login, ct);

    public async Task<long> InsertAsync(StoredUser u, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (login, full_name, role, is_active, must_change, password_hash, failed_attempts, locked_until, created_at, last_login_at)
            VALUES ($login, $name, $role, $active, $must, $hash, $failed, $locked, $created, $last);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, u);
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new DuplicateException($"Логин «{u.User.Login}» уже занят.");
        }
    }

    public async Task UpdateAsync(StoredUser u, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET full_name = $name, role = $role, is_active = $active, must_change = $must,
                password_hash = $hash, failed_attempts = $failed, locked_until = $locked, last_login_at = $last
            WHERE id = $id
            """;
        Bind(cmd, u);
        cmd.Parameters.AddWithValue("$id", u.User.Id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            throw new KeyNotFoundException($"Пользователь {u.User.Id} не найден.");
        }
    }

    private async Task<StoredUser?> QuerySingleAsync(string where, object value, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM users WHERE {where}";
        cmd.Parameters.AddWithValue("$v", value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static void Bind(SqliteCommand cmd, StoredUser u)
    {
        cmd.Parameters.AddWithValue("$login", u.User.Login);
        cmd.Parameters.AddWithValue("$name", u.User.FullName);
        cmd.Parameters.AddWithValue("$role", (int)u.User.Role);
        cmd.Parameters.AddWithValue("$active", u.User.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$must", u.User.MustChangePassword ? 1 : 0);
        cmd.Parameters.AddWithValue("$hash", u.PasswordHash);
        cmd.Parameters.AddWithValue("$failed", u.FailedAttempts);
        cmd.Parameters.AddWithValue("$locked", u.LockedUntil is { } l ? l.ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$created", u.User.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$last", u.User.LastLoginAt is { } t ? t.ToUnixTimeMilliseconds() : DBNull.Value);
    }

    private static StoredUser Read(SqliteDataReader r) => new(
        new User
        {
            Id = r.GetInt64(0),
            Login = r.GetString(1),
            FullName = r.GetString(2),
            Role = (UserRole)r.GetInt32(3),
            IsActive = r.GetInt32(4) != 0,
            MustChangePassword = r.GetInt32(5) != 0,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)),
            LastLoginAt = r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)),
        },
        r.GetString(6),
        r.GetInt32(7),
        r.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)));
}

public sealed class SqliteAuditLog(SqliteDatabase db, TimeProvider time) : IAuditLog
{
    public async Task WriteAsync(string user, string action, string details, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_log (time, user, action, details) VALUES ($t, $u, $a, $d)";
        cmd.Parameters.AddWithValue("$t", time.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$d", details);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditRecord>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, time, user, action, details FROM audit_log ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        var list = new List<AuditRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AuditRecord(
                r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).ToLocalTime(), r.GetString(2), r.GetString(3), r.GetString(4)));
        }

        return list;
    }
}
