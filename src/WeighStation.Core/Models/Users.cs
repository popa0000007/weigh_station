namespace WeighStation.Core.Models;

public enum UserRole
{
    Operator = 1,
    Manager = 2,
    Administrator = 3,
}

public sealed record User
{
    public long Id { get; init; }
    public required string Login { get; init; }
    public required string FullName { get; init; }
    public UserRole Role { get; init; } = UserRole.Operator;
    public bool IsActive { get; init; } = true;
    /// <summary>Set after an administrator created the account or reset the password: the user must choose their own at the next login.</summary>
    public bool MustChangePassword { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }

    /// <summary>The name written into weighing histories.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Login : FullName;
}

/// <summary>A user together with the security-sensitive columns that never leave the service layer.</summary>
public sealed record StoredUser(User User, string PasswordHash, int FailedAttempts, DateTimeOffset? LockedUntil);

public sealed record AuditRecord(long Id, DateTimeOffset Time, string User, string Action, string Details);
