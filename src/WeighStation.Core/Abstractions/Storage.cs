using WeighStation.Core.Models;

namespace WeighStation.Core.Abstractions;

public sealed record WeighingQuery
{
    public DateTimeOffset? From { get; init; }
    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset? To { get; init; }
    /// <summary>Substring match on the normalised plate.</summary>
    public string? Plate { get; init; }
    /// <summary>Case-insensitive substring match.</summary>
    public string? Company { get; init; }
    public WeighingStatus? Status { get; init; }
    public int Limit { get; init; } = 1000;
}

public interface IWeighingRepository
{
    /// <summary>Stores the weighing with its initial history and returns it with the generated id.</summary>
    Task<Weighing> AddAsync(Weighing weighing, IReadOnlyList<AuditEntry> history, CancellationToken ct = default);
    Task<IReadOnlyList<Weighing>> GetRecentAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<Weighing>> QueryAsync(WeighingQuery query, CancellationToken ct = default);
    Task<Weighing?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Replaces the descriptive fields (plate, trailer, driver, company, cargo) of a weighing, marks it
    /// as manually edited and appends <paramref name="entry"/> to its history. Weights cannot be changed.
    /// </summary>
    Task UpdateDescriptionAsync(Weighing edited, AuditEntry entry, CancellationToken ct = default);

    /// <summary>Marks the weighing as cancelled: kept in the journal, excluded from reports and tare lookup.</summary>
    Task CancelAsync(long id, AuditEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> GetHistoryAsync(long id, CancellationToken ct = default);

    /// <summary>Adds historical weighings (with one history line each) in a single transaction. Returns how many were added.</summary>
    Task<int> ImportAsync(IReadOnlyList<(Weighing Weighing, AuditEntry Entry)> items, IProgress<int>? progress = null, CancellationToken ct = default);
}

public interface ITareProvider
{
    /// <summary>Last known tare of the vehicle, or null if nothing is known about it.</summary>
    Task<int?> FindTareKgAsync(string plate, CancellationToken ct = default);
}

public interface IReferenceStore<T> where T : class
{
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts when <c>Id == 0</c>, otherwise updates. Returns the id.</summary>
    Task<long> SaveAsync(T item, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Inserts (<c>Id == 0</c>) or updates many items in one transaction: all or nothing, and one pass instead of one
    /// round trip per item. The caller guarantees the items don't clash with each other or with other records.
    /// </summary>
    Task BulkSaveAsync(IReadOnlyList<T> items, IProgress<int>? progress = null, CancellationToken ct = default);
}

public interface IVehicleStore : IReferenceStore<Vehicle>
{
    Task<Vehicle?> FindByPlateAsync(string plate, CancellationToken ct = default);
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface IDatabaseBackup
{
    string DatabasePath { get; }

    /// <summary>Writes a consistent copy of the live database to <paramref name="targetPath"/>.</summary>
    Task BackupAsync(string targetPath, CancellationToken ct = default);
}
