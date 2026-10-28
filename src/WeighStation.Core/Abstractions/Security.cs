using WeighStation.Core.Models;

namespace WeighStation.Core.Abstractions;

public interface IUserStore
{
    Task<IReadOnlyList<StoredUser>> GetAllAsync(CancellationToken ct = default);

    Task<StoredUser?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Case-insensitive lookup.</summary>
    Task<StoredUser?> FindByLoginAsync(string login, CancellationToken ct = default);

    /// <summary>Inserts a new user (Id is ignored) and returns the generated id. Throws <see cref="DuplicateException"/> if the login is taken.</summary>
    Task<long> InsertAsync(StoredUser user, CancellationToken ct = default);

    Task UpdateAsync(StoredUser user, CancellationToken ct = default);
}

public interface IAuditLog
{
    Task WriteAsync(string user, string action, string details, CancellationToken ct = default);

    Task<IReadOnlyList<AuditRecord>> GetRecentAsync(int count, CancellationToken ct = default);
}

/// <summary>A user-facing validation problem; the message is shown to the person as is.</summary>
public sealed class ValidationException(string message) : Exception(message);
