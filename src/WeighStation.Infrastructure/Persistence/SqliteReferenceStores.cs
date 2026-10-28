using Microsoft.Data.Sqlite;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Infrastructure.Persistence;

/// <summary>
/// The four reference books differ only in their columns, so the SQL lives here once and each subclass
/// just says which columns it has and how to turn a row into a record and back.
/// </summary>
public abstract class SqliteReferenceStore<T>(SqliteDatabase db, string table, string[] columns) : IReferenceStore<T>
    where T : class
{
    private static readonly StringComparer Ru = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), true);

    protected SqliteDatabase Db { get; } = db;

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, {string.Join(", ", columns)} FROM {table}";
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        // SQLite can't sort Cyrillic case-insensitively, so sort here.
        return list.OrderBy(SortKey, Ru).ToList();
    }

    public async Task<long> SaveAsync(T item, CancellationToken ct = default)
    {
        item = Normalize(item);
        await EnsureUniqueAsync(item, ct);

        await using var conn = await Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var values = Values(item);
        var id = IdOf(item);
        cmd.CommandText = id == 0
            ? $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "$" + c))}); SELECT last_insert_rowid();"
            : $"UPDATE {table} SET {string.Join(", ", columns.Select(c => $"{c} = ${c}"))} WHERE id = $id; SELECT $id;";
        for (var i = 0; i < columns.Length; i++)
        {
            cmd.Parameters.AddWithValue("$" + columns[i], values[i] ?? DBNull.Value);
        }

        cmd.Parameters.AddWithValue("$id", id);
        try
        {
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new DuplicateException("Такая запись уже существует.");
        }
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    protected abstract T Read(SqliteDataReader r);

    /// <summary>Values in the same order as <c>columns</c>.</summary>
    protected abstract object?[] Values(T item);

    protected abstract long IdOf(T item);

    protected abstract string SortKey(T item);

    /// <summary>Canonical form to store (trimmed text, normalised plate).</summary>
    protected virtual T Normalize(T item) => item;

    /// <summary>The key a duplicate check compares on, or null when duplicates are fine.</summary>
    protected virtual string? UniqueKey(T item) => SortKey(item);

    private async Task EnsureUniqueAsync(T item, CancellationToken ct)
    {
        var key = UniqueKey(item);
        if (key is null)
        {
            return;
        }

        foreach (var other in await GetAllAsync(ct))
        {
            if (IdOf(other) != IdOf(item) && string.Equals(UniqueKey(other), key, StringComparison.CurrentCultureIgnoreCase))
            {
                throw new DuplicateException($"«{key}» уже есть в справочнике.");
            }
        }
    }

    protected static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    protected static int? Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    protected static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class SqliteVehicleStore(SqliteDatabase db)
    : SqliteReferenceStore<Vehicle>(db, "vehicles", ["plate", "trailer", "driver", "company", "cargo", "tare_kg", "max_mass_kg", "note"]),
      IVehicleStore
{
    public async Task<Vehicle?> FindByPlateAsync(string plate, CancellationToken ct = default)
    {
        var normalized = PlateNumber.Normalize(plate);
        return normalized.Length == 0
            ? null
            : (await GetAllAsync(ct)).FirstOrDefault(v => v.Plate == normalized);
    }

    protected override Vehicle Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Plate = r.GetString(1),
        Trailer = Text(r, 2),
        Driver = Text(r, 3),
        Company = Text(r, 4),
        Cargo = Text(r, 5),
        TareKg = Number(r, 6),
        MaxMassKg = Number(r, 7),
        Note = Text(r, 8),
    };

    protected override object?[] Values(Vehicle v) =>
        [v.Plate, v.Trailer, v.Driver, v.Company, v.Cargo, v.TareKg, v.MaxMassKg, v.Note];

    protected override long IdOf(Vehicle v) => v.Id;

    protected override string SortKey(Vehicle v) => v.Plate;

    protected override Vehicle Normalize(Vehicle v) => v with
    {
        Plate = PlateNumber.Normalize(v.Plate),
        Trailer = Clean(PlateNumber.Normalize(v.Trailer)),
        Driver = Clean(v.Driver),
        Company = Clean(v.Company),
        Cargo = Clean(v.Cargo),
        Note = Clean(v.Note),
    };
}

public sealed class SqliteCompanyStore(SqliteDatabase db)
    : SqliteReferenceStore<Company>(db, "companies", ["name", "inn", "note"])
{
    protected override Company Read(SqliteDataReader r) =>
        new() { Id = r.GetInt64(0), Name = r.GetString(1), Inn = Text(r, 2), Note = Text(r, 3) };

    protected override object?[] Values(Company c) => [c.Name, c.Inn, c.Note];

    protected override long IdOf(Company c) => c.Id;

    protected override string SortKey(Company c) => c.Name;

    protected override Company Normalize(Company c) => c with { Name = c.Name.Trim(), Inn = Clean(c.Inn), Note = Clean(c.Note) };
}

public sealed class SqliteCargoStore(SqliteDatabase db)
    : SqliteReferenceStore<CargoType>(db, "cargos", ["name"])
{
    protected override CargoType Read(SqliteDataReader r) => new() { Id = r.GetInt64(0), Name = r.GetString(1) };

    protected override object?[] Values(CargoType c) => [c.Name];

    protected override long IdOf(CargoType c) => c.Id;

    protected override string SortKey(CargoType c) => c.Name;

    protected override CargoType Normalize(CargoType c) => c with { Name = c.Name.Trim() };
}

public sealed class SqliteDriverStore(SqliteDatabase db)
    : SqliteReferenceStore<Driver>(db, "drivers", ["full_name", "phone"])
{
    protected override Driver Read(SqliteDataReader r) =>
        new() { Id = r.GetInt64(0), FullName = r.GetString(1), Phone = Text(r, 2) };

    protected override object?[] Values(Driver d) => [d.FullName, d.Phone];

    protected override long IdOf(Driver d) => d.Id;

    protected override string SortKey(Driver d) => d.FullName;

    protected override Driver Normalize(Driver d) => d with { FullName = d.FullName.Trim(), Phone = Clean(d.Phone) };
}
