using System.Text.RegularExpressions;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Core.Security;

public enum LoginStatus { Success, InvalidCredentials, Locked, Inactive }

public sealed record LoginResult(LoginStatus Status, User? User = null, DateTimeOffset? LockedUntil = null);

public sealed record AuthOptions
{
    public int MaxFailedAttempts { get; init; } = 5;
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Accounts, passwords and who may manage them. Rules enforced here (not only in the screens):
/// a login locks after repeated failures, the last administrator can't be removed, and every
/// administrative action checks the acting user's role.
/// </summary>
public sealed partial class UserService(
    IUserStore store, IPasswordHasher hasher, IAuditLog audit, TimeProvider time, AuthOptions? options = null)
{
    private readonly AuthOptions _options = options ?? new AuthOptions();

    // Verified for unknown logins too, so response time doesn't reveal whether a login exists.
    private readonly string _dummyHash = hasher.Hash("not-a-real-password");

    public async Task<bool> HasUsersAsync(CancellationToken ct = default) => (await store.GetAllAsync(ct)).Count > 0;

    /// <summary>First start: the only way to get an account without an administrator. Refuses once any user exists.</summary>
    public async Task<User> CreateFirstAdministratorAsync(string login, string fullName, string password, CancellationToken ct = default)
    {
        if (await HasUsersAsync(ct))
        {
            throw new InvalidOperationException("Администратор уже создан.");
        }

        var user = await InsertAsync(login, fullName, UserRole.Administrator, password, mustChange: false, ct);
        await audit.WriteAsync(user.Login, "user_created", "Первый администратор создан при первом запуске", ct);
        return user;
    }

    public async Task<LoginResult> LoginAsync(string login, string password, CancellationToken ct = default)
    {
        login = login.Trim();
        var stored = login.Length == 0 ? null : await store.FindByLoginAsync(login, ct);
        var now = time.GetUtcNow();

        if (stored is null)
        {
            hasher.Verify(password, _dummyHash);
            await audit.WriteAsync(login, "login_failed", "Неизвестный логин", ct);
            return new LoginResult(LoginStatus.InvalidCredentials);
        }

        if (stored.LockedUntil is { } until && until > now)
        {
            await audit.WriteAsync(stored.User.Login, "login_blocked", "Попытка входа в заблокированную учётную запись", ct);
            return new LoginResult(LoginStatus.Locked, LockedUntil: until);
        }

        if (!hasher.Verify(password, stored.PasswordHash))
        {
            var failures = stored.FailedAttempts + 1;
            DateTimeOffset? lockUntil = null;
            if (failures >= _options.MaxFailedAttempts)
            {
                lockUntil = now + _options.LockDuration;
                failures = 0;
                await audit.WriteAsync(stored.User.Login, "account_locked", $"Заблокирована на {_options.LockDuration.TotalMinutes:0} мин после неудачных попыток", ct);
            }

            await store.UpdateAsync(stored with { FailedAttempts = failures, LockedUntil = lockUntil }, ct);
            await audit.WriteAsync(stored.User.Login, "login_failed", "Неверный пароль", ct);
            return lockUntil is null
                ? new LoginResult(LoginStatus.InvalidCredentials)
                : new LoginResult(LoginStatus.Locked, LockedUntil: lockUntil);
        }

        if (!stored.User.IsActive)
        {
            await audit.WriteAsync(stored.User.Login, "login_failed", "Учётная запись отключена", ct);
            return new LoginResult(LoginStatus.Inactive);
        }

        var user = stored.User with { LastLoginAt = now };
        await store.UpdateAsync(stored with { User = user, FailedAttempts = 0, LockedUntil = null }, ct);
        await audit.WriteAsync(user.Login, "login", "Вход выполнен", ct);
        return new LoginResult(LoginStatus.Success, user);
    }

    public Task RecordLogoutAsync(User user, CancellationToken ct = default) =>
        audit.WriteAsync(user.Login, "logout", "Выход из программы", ct);

    public async Task<IReadOnlyList<User>> GetUsersAsync(User actor, CancellationToken ct = default)
    {
        Authorize(actor);
        return (await store.GetAllAsync(ct)).Select(s => s.User).OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>When the account is locked: until when (for the administrators' list).</summary>
    public async Task<IReadOnlyDictionary<long, DateTimeOffset>> GetLocksAsync(User actor, CancellationToken ct = default)
    {
        Authorize(actor);
        var now = time.GetUtcNow();
        return (await store.GetAllAsync(ct))
            .Where(s => s.LockedUntil > now)
            .ToDictionary(s => s.User.Id, s => s.LockedUntil!.Value);
    }

    public async Task<User> CreateAsync(
        User actor, string login, string fullName, UserRole role, string password, CancellationToken ct = default)
    {
        Authorize(actor);
        var user = await InsertAsync(login, fullName, role, password, mustChange: true, ct);
        await audit.WriteAsync(actor.Login, "user_created", $"Создан {user.Login} ({RolePermissions.Name(role)})", ct);
        return user;
    }

    public async Task<User> UpdateAsync(
        User actor, long id, string fullName, UserRole role, bool isActive, CancellationToken ct = default)
    {
        Authorize(actor);
        var current = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Пользователь {id} не найден.");
        fullName = RequireName(fullName);

        var losesAdmin = current.User is { Role: UserRole.Administrator, IsActive: true } && (role != UserRole.Administrator || !isActive);
        if (losesAdmin && !(await store.GetAllAsync(ct)).Any(s => s.User.Id != id && s.User is { Role: UserRole.Administrator, IsActive: true }))
        {
            throw new InvalidOperationException("Нельзя отключить или понизить последнего администратора.");
        }

        var updated = current.User with { FullName = fullName, Role = role, IsActive = isActive };
        await store.UpdateAsync(current with { User = updated }, ct);

        var changes = new List<string>();
        if (current.User.FullName != fullName) changes.Add($"имя «{current.User.FullName}» → «{fullName}»");
        if (current.User.Role != role) changes.Add($"роль {RolePermissions.Name(current.User.Role)} → {RolePermissions.Name(role)}");
        if (current.User.IsActive != isActive) changes.Add(isActive ? "включён" : "отключён");
        if (changes.Count > 0)
        {
            await audit.WriteAsync(actor.Login, "user_updated", $"{current.User.Login}: {string.Join("; ", changes)}", ct);
        }

        return updated;
    }

    /// <summary>The administrator sets a temporary password; the user must replace it at the next login.</summary>
    public async Task ResetPasswordAsync(User actor, long id, string newPassword, CancellationToken ct = default)
    {
        Authorize(actor);
        var current = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Пользователь {id} не найден.");
        RequirePassword(newPassword, current.User.Login);
        await store.UpdateAsync(
            current with
            {
                User = current.User with { MustChangePassword = true },
                PasswordHash = hasher.Hash(newPassword),
                FailedAttempts = 0,
                LockedUntil = null,
            }, ct);
        await audit.WriteAsync(actor.Login, "password_reset", $"Сброшен пароль {current.User.Login}", ct);
    }

    public async Task UnlockAsync(User actor, long id, CancellationToken ct = default)
    {
        Authorize(actor);
        var current = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Пользователь {id} не найден.");
        await store.UpdateAsync(current with { FailedAttempts = 0, LockedUntil = null }, ct);
        await audit.WriteAsync(actor.Login, "user_unlocked", $"Разблокирован {current.User.Login}", ct);
    }

    /// <summary>A user changes their own password; the old one must be correct.</summary>
    public async Task<User> ChangePasswordAsync(long userId, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        var current = await store.GetAsync(userId, ct) ?? throw new KeyNotFoundException($"Пользователь {userId} не найден.");
        if (!hasher.Verify(oldPassword, current.PasswordHash))
        {
            await audit.WriteAsync(current.User.Login, "password_change_failed", "Неверный текущий пароль", ct);
            throw new ValidationException("Текущий пароль указан неверно.");
        }

        RequirePassword(newPassword, current.User.Login);
        if (hasher.Verify(newPassword, current.PasswordHash))
        {
            throw new ValidationException("Новый пароль должен отличаться от текущего.");
        }

        var user = current.User with { MustChangePassword = false };
        await store.UpdateAsync(current with { User = user, PasswordHash = hasher.Hash(newPassword) }, ct);
        await audit.WriteAsync(user.Login, "password_changed", "Пароль изменён пользователем", ct);
        return user;
    }

    private async Task<User> InsertAsync(
        string login, string fullName, UserRole role, string password, bool mustChange, CancellationToken ct)
    {
        login = login.Trim();
        if (!LoginFormat().IsMatch(login))
        {
            throw new ValidationException("Логин: от 3 до 32 символов — латинские буквы, цифры, точка, дефис и подчёркивание.");
        }

        fullName = RequireName(fullName);
        RequirePassword(password, login);

        var user = new User
        {
            Login = login,
            FullName = fullName,
            Role = role,
            MustChangePassword = mustChange,
            CreatedAt = time.GetUtcNow(),
        };
        try
        {
            var id = await store.InsertAsync(new StoredUser(user, hasher.Hash(password), 0, null), ct);
            return user with { Id = id };
        }
        catch (DuplicateException)
        {
            throw new ValidationException($"Логин «{login}» уже занят.");
        }
    }

    private static string RequireName(string fullName)
    {
        fullName = fullName.Trim();
        return fullName.Length == 0 ? throw new ValidationException("Укажите имя пользователя.") : fullName;
    }

    private static void RequirePassword(string password, string login)
    {
        if (PasswordPolicy.Check(password, login) is { } problem)
        {
            throw new ValidationException(problem);
        }
    }

    private static void Authorize(User actor)
    {
        if (!actor.IsActive || !RolePermissions.Has(actor.Role, Permission.ManageUsers))
        {
            throw new UnauthorizedAccessException("Недостаточно прав для управления пользователями.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,32}$")]
    private static partial Regex LoginFormat();
}
