using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Core.Security;
using WeighStation.Core.Tests.Presentation;
using WeighStation.Infrastructure.Persistence;

namespace WeighStation.Core.Tests;

public class PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new(iterations: 1000);

    [Fact]
    public void Verifies_the_right_password_only()
    {
        var stored = _hasher.Hash("Correct-Horse-1");
        Assert.True(_hasher.Verify("Correct-Horse-1", stored));
        Assert.False(_hasher.Verify("Correct-Horse-2", stored));
        Assert.False(_hasher.Verify("", stored));
    }

    [Fact]
    public void Same_password_gives_different_hashes_because_of_the_salt()
    {
        Assert.NotEqual(_hasher.Hash("Correct-Horse-1"), _hasher.Hash("Correct-Horse-1"));
    }

    [Fact]
    public void Stored_string_carries_its_parameters_and_hides_the_password()
    {
        var stored = _hasher.Hash("Correct-Horse-1");
        Assert.StartsWith("pbkdf2-sha256$1000$", stored);
        Assert.DoesNotContain("Correct", stored);
    }

    [Fact]
    public void A_hash_made_with_other_iterations_still_verifies()
    {
        var old = new Pbkdf2PasswordHasher(500).Hash("Correct-Horse-1");
        Assert.True(_hasher.Verify("Correct-Horse-1", old));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$x$y$z")]
    [InlineData("pbkdf2-sha256$1000$!!!$!!!")]
    [InlineData("other$1000$AAAA$AAAA")]
    public void Malformed_stored_values_never_verify_and_never_throw(string stored) =>
        Assert.False(_hasher.Verify("anything", stored));

    [Fact]
    public void Default_cost_is_at_least_the_current_recommendation() =>
        Assert.True(Pbkdf2PasswordHasher.DefaultIterations >= 600_000);
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short1", "ivan", "не короче")]
    [InlineData("", "ivan", "не короче")]
    [InlineData("aaaaaaaaaa", "ivan", "слишком простой")]
    [InlineData("abababab", "ivan", "слишком простой")]
    [InlineData("my-ivan-pass", "ivan", "логин")]
    [InlineData("MY-IVAN-PASS", "ivan", "логин")]
    public void Weak_passwords_are_rejected_with_a_reason(string password, string login, string expected) =>
        Assert.Contains(expected, PasswordPolicy.Check(password, login));

    [Theory]
    [InlineData("Weighbridge-2026")]
    [InlineData("пароль-на-русском")]
    [InlineData("12345678a")]
    public void Reasonable_passwords_pass(string password) => Assert.Null(PasswordPolicy.Check(password, "ivan"));
}

public class RolePermissionsTests
{
    [Fact]
    public void Administrator_has_every_permission() =>
        Assert.All(Enum.GetValues<Permission>(), p => Assert.True(RolePermissions.Has(UserRole.Administrator, p), p.ToString()));

    [Fact]
    public void Roles_are_strictly_nested()
    {
        Assert.True(RolePermissions.Of(UserRole.Operator).IsProperSubsetOf(RolePermissions.Of(UserRole.Manager)));
        Assert.True(RolePermissions.Of(UserRole.Manager).IsProperSubsetOf(RolePermissions.Of(UserRole.Administrator)));
    }

    [Theory]
    [InlineData(UserRole.Operator, Permission.Weigh, true)]
    [InlineData(UserRole.Operator, Permission.ControlBarrier, true)]
    [InlineData(UserRole.Operator, Permission.ViewJournal, true)]
    [InlineData(UserRole.Operator, Permission.EditJournal, false)]
    [InlineData(UserRole.Operator, Permission.ExportData, false)]
    [InlineData(UserRole.Operator, Permission.ViewReports, false)]
    [InlineData(UserRole.Operator, Permission.ViewReferences, false)]
    [InlineData(UserRole.Operator, Permission.ManageUsers, false)]
    [InlineData(UserRole.Manager, Permission.EditJournal, true)]
    [InlineData(UserRole.Manager, Permission.ViewReports, true)]
    [InlineData(UserRole.Manager, Permission.EditReferences, true)]
    [InlineData(UserRole.Manager, Permission.ViewDevices, true)]
    [InlineData(UserRole.Manager, Permission.EditDevices, false)]
    [InlineData(UserRole.Manager, Permission.EditSettings, false)]
    [InlineData(UserRole.Manager, Permission.ManageUsers, false)]
    [InlineData(UserRole.Manager, Permission.ImportData, false)]
    public void Matrix(UserRole role, Permission permission, bool expected) =>
        Assert.Equal(expected, RolePermissions.Has(role, permission));

    [Fact]
    public void Every_role_has_a_name_and_description() =>
        Assert.All(Enum.GetValues<UserRole>(), r =>
        {
            Assert.NotEqual(r.ToString(), RolePermissions.Name(r));
            Assert.NotEmpty(RolePermissions.Describe(r));
        });
}

public class CurrentUserTests
{
    private static User U(UserRole role) => new() { Id = 1, Login = "u", FullName = "Иванов", Role = role };

    [Fact]
    public void Nobody_signed_in_can_do_nothing()
    {
        var current = new CurrentUser();
        Assert.False(current.IsSignedIn);
        Assert.All(Enum.GetValues<Permission>(), p => Assert.False(current.Can(p)));
        Assert.Equal("—", current.DisplayName);
    }

    [Fact]
    public void Signing_in_and_out_changes_permissions_and_notifies()
    {
        var current = new CurrentUser();
        var notified = 0;
        current.Changed += (_, _) => notified++;

        current.SignIn(U(UserRole.Operator));
        Assert.True(current.Can(Permission.Weigh));
        Assert.False(current.Can(Permission.ManageUsers));
        Assert.Equal("Иванов", current.DisplayName);

        current.SignIn(U(UserRole.Administrator));
        Assert.True(current.Can(Permission.ManageUsers));

        current.SignOut();
        Assert.False(current.Can(Permission.Weigh));
        Assert.Equal(3, notified);
    }

    [Fact]
    public void Display_name_falls_back_to_login()
    {
        var current = new CurrentUser();
        current.SignIn(U(UserRole.Operator) with { FullName = " " });
        Assert.Equal("u", current.DisplayName);
    }
}

public class UserServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weigh-sec-" + Guid.NewGuid().ToString("N"));
    private readonly TestTime _time = new();
    private readonly SqliteDatabase _db;
    private readonly SqliteUserStore _store;
    private readonly SqliteAuditLog _audit;
    private readonly UserService _service;
    private User _admin = null!;

    public UserServiceTests()
    {
        _db = new SqliteDatabase(Path.Combine(_dir, "sec.db"));
        _store = new SqliteUserStore(_db);
        _audit = new SqliteAuditLog(_db, _time);
        _service = new UserService(_store, new Pbkdf2PasswordHasher(1000), _audit, _time, new AuthOptions { MaxFailedAttempts = 3, LockDuration = TimeSpan.FromMinutes(10) });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private async Task<User> SetUpAdminAsync() =>
        _admin = await _service.CreateFirstAdministratorAsync("admin", "Главный Администратор", "Scale-Pass-1");

    private async Task<User> AddAsync(string login, UserRole role, string password = "Temp-Pass-123") =>
        await _service.CreateAsync(_admin, login, $"Пользователь {login}", role, password);

    private async Task<IReadOnlyList<string>> ActionsAsync() => (await _audit.GetRecentAsync(100)).Select(a => a.Action).Reverse().ToList();

    [Fact]
    public async Task First_administrator_can_be_created_only_once()
    {
        Assert.False(await _service.HasUsersAsync());
        var admin = await SetUpAdminAsync();

        Assert.True(await _service.HasUsersAsync());
        Assert.Equal(UserRole.Administrator, admin.Role);
        Assert.False(admin.MustChangePassword);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateFirstAdministratorAsync("other", "Другой", "Other-Pass-1"));
    }

    [Theory]
    [InlineData("ab", "Имя", "Strong-Pass-1", "Логин")]
    [InlineData("has space", "Имя", "Strong-Pass-1", "Логин")]
    [InlineData("кириллица", "Имя", "Strong-Pass-1", "Логин")]
    [InlineData("valid.login", "  ", "Strong-Pass-1", "имя")]
    [InlineData("valid.login", "Имя", "short", "не короче")]
    public async Task Creating_an_account_validates_input(string login, string name, string password, string expected)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.CreateFirstAdministratorAsync(login, name, password));
        Assert.Contains(expected, ex.Message);
        Assert.False(await _service.HasUsersAsync());
    }

    [Fact]
    public async Task Password_is_stored_hashed_not_plain()
    {
        await SetUpAdminAsync();
        var stored = (await _store.FindByLoginAsync("admin"))!;
        Assert.DoesNotContain("Scale-Pass-1", stored.PasswordHash);
        Assert.StartsWith("pbkdf2-sha256$", stored.PasswordHash);
    }

    [Fact]
    public async Task Login_succeeds_ignoring_login_case_and_records_the_time()
    {
        await SetUpAdminAsync();
        _time.Now += TimeSpan.FromHours(1);

        var result = await _service.LoginAsync(" ADMIN ", "Scale-Pass-1");

        Assert.Equal(LoginStatus.Success, result.Status);
        Assert.Equal("admin", result.User!.Login);
        Assert.Equal(_time.Now, result.User.LastLoginAt);
        Assert.Equal(_time.Now, (await _store.FindByLoginAsync("admin"))!.User.LastLoginAt);
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("nobody", "Scale-Pass-1")]
    [InlineData("", "")]
    [InlineData("admin", "")]
    public async Task Wrong_credentials_give_the_same_answer_whatever_is_wrong(string login, string password)
    {
        await SetUpAdminAsync();
        var result = await _service.LoginAsync(login, password);
        Assert.Equal(LoginStatus.InvalidCredentials, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_even_for_the_right_password_then_unlocks_by_time()
    {
        await SetUpAdminAsync();

        Assert.Equal(LoginStatus.InvalidCredentials, (await _service.LoginAsync("admin", "bad-1")).Status);
        Assert.Equal(LoginStatus.InvalidCredentials, (await _service.LoginAsync("admin", "bad-2")).Status);
        var third = await _service.LoginAsync("admin", "bad-3");

        Assert.Equal(LoginStatus.Locked, third.Status);
        Assert.Equal(_time.Now + TimeSpan.FromMinutes(10), third.LockedUntil);
        Assert.Equal(LoginStatus.Locked, (await _service.LoginAsync("admin", "Scale-Pass-1")).Status);

        _time.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(LoginStatus.Success, (await _service.LoginAsync("admin", "Scale-Pass-1")).Status);
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_counter()
    {
        await SetUpAdminAsync();
        await _service.LoginAsync("admin", "bad-1");
        await _service.LoginAsync("admin", "bad-2");
        await _service.LoginAsync("admin", "Scale-Pass-1");

        await _service.LoginAsync("admin", "bad-3");
        await _service.LoginAsync("admin", "bad-4");
        Assert.Equal(LoginStatus.Success, (await _service.LoginAsync("admin", "Scale-Pass-1")).Status);
    }

    [Fact]
    public async Task Disabled_account_cannot_log_in_but_only_the_right_password_learns_why()
    {
        await SetUpAdminAsync();
        var op = await AddAsync("ivan", UserRole.Operator);
        await _service.UpdateAsync(_admin, op.Id, op.FullName, op.Role, isActive: false);

        Assert.Equal(LoginStatus.Inactive, (await _service.LoginAsync("ivan", "Temp-Pass-123")).Status);
        Assert.Equal(LoginStatus.InvalidCredentials, (await _service.LoginAsync("ivan", "wrong")).Status);
    }

    [Fact]
    public async Task Administrator_creates_users_who_must_change_the_temporary_password()
    {
        await SetUpAdminAsync();
        var op = await AddAsync("ivan", UserRole.Operator);

        Assert.True(op.MustChangePassword);
        var login = await _service.LoginAsync("ivan", "Temp-Pass-123");
        Assert.True(login.User!.MustChangePassword);

        var changed = await _service.ChangePasswordAsync(op.Id, "Temp-Pass-123", "My-Own-Pass-9");

        Assert.False(changed.MustChangePassword);
        Assert.Equal(LoginStatus.InvalidCredentials, (await _service.LoginAsync("ivan", "Temp-Pass-123")).Status);
        var again = await _service.LoginAsync("ivan", "My-Own-Pass-9");
        Assert.Equal(LoginStatus.Success, again.Status);
        Assert.False(again.User!.MustChangePassword);
    }

    [Fact]
    public async Task Duplicate_login_is_reported_in_any_case()
    {
        await SetUpAdminAsync();
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.CreateAsync(_admin, "ADMIN", "Дубль", UserRole.Operator, "Temp-Pass-123"));
        Assert.Contains("уже занят", ex.Message);
    }

    [Fact]
    public async Task Changing_password_needs_the_old_one_and_a_new_different_acceptable_one()
    {
        await SetUpAdminAsync();

        var wrongOld = await Assert.ThrowsAsync<ValidationException>(() => _service.ChangePasswordAsync(_admin.Id, "nope", "Brand-New-Pass-1"));
        Assert.Contains("Текущий пароль", wrongOld.Message);

        var same = await Assert.ThrowsAsync<ValidationException>(() => _service.ChangePasswordAsync(_admin.Id, "Scale-Pass-1", "Scale-Pass-1"));
        Assert.Contains("отличаться", same.Message);

        var weak = await Assert.ThrowsAsync<ValidationException>(() => _service.ChangePasswordAsync(_admin.Id, "Scale-Pass-1", "123"));
        Assert.Contains("не короче", weak.Message);

        await _service.ChangePasswordAsync(_admin.Id, "Scale-Pass-1", "Brand-New-Pass-1");
        Assert.Equal(LoginStatus.Success, (await _service.LoginAsync("admin", "Brand-New-Pass-1")).Status);
    }

    [Fact]
    public async Task Reset_password_unlocks_and_forces_a_change()
    {
        await SetUpAdminAsync();
        var op = await AddAsync("ivan", UserRole.Operator);
        for (var i = 0; i < 3; i++)
        {
            await _service.LoginAsync("ivan", "bad");
        }

        Assert.Equal(LoginStatus.Locked, (await _service.LoginAsync("ivan", "Temp-Pass-123")).Status);

        await _service.ResetPasswordAsync(_admin, op.Id, "Another-Temp-77");

        var result = await _service.LoginAsync("ivan", "Another-Temp-77");
        Assert.Equal(LoginStatus.Success, result.Status);
        Assert.True(result.User!.MustChangePassword);
    }

    [Fact]
    public async Task Unlock_lets_the_user_in_immediately()
    {
        await SetUpAdminAsync();
        var op = await AddAsync("ivan", UserRole.Operator);
        for (var i = 0; i < 3; i++)
        {
            await _service.LoginAsync("ivan", "bad");
        }

        var locks = await _service.GetLocksAsync(_admin);
        Assert.Contains(op.Id, locks.Keys);

        await _service.UnlockAsync(_admin, op.Id);

        Assert.Empty(await _service.GetLocksAsync(_admin));
        Assert.Equal(LoginStatus.Success, (await _service.LoginAsync("ivan", "Temp-Pass-123")).Status);
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_demoted_or_disabled_but_a_second_one_makes_it_possible()
    {
        await SetUpAdminAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateAsync(_admin, _admin.Id, _admin.FullName, UserRole.Manager, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateAsync(_admin, _admin.Id, _admin.FullName, UserRole.Administrator, false));

        var second = await AddAsync("boss", UserRole.Administrator);
        await _service.UpdateAsync(_admin, _admin.Id, _admin.FullName, UserRole.Manager, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateAsync(second, second.Id, second.FullName, UserRole.Operator, true));
    }

    [Fact]
    public async Task Only_administrators_manage_users()
    {
        await SetUpAdminAsync();
        var manager = await AddAsync("mgr", UserRole.Manager);
        var op = await AddAsync("ivan", UserRole.Operator);

        foreach (var actor in new[] { manager, op })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetUsersAsync(actor));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateAsync(actor, "new.user", "Новый", UserRole.Operator, "Temp-Pass-123"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.UpdateAsync(actor, op.Id, "X", UserRole.Administrator, true));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ResetPasswordAsync(actor, _admin.Id, "Hacked-Pass-1"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.UnlockAsync(actor, op.Id));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetLocksAsync(actor));
        }

        Assert.Equal(LoginStatus.Success, (await _service.LoginAsync("admin", "Scale-Pass-1")).Status);
    }

    [Fact]
    public async Task A_disabled_administrator_loses_the_right_to_manage_users()
    {
        await SetUpAdminAsync();
        var second = await AddAsync("boss", UserRole.Administrator);
        await _service.UpdateAsync(_admin, second.Id, second.FullName, UserRole.Administrator, false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetUsersAsync(second with { IsActive = false }));
    }

    [Fact]
    public async Task Users_are_listed_alphabetically_by_login()
    {
        await SetUpAdminAsync();
        await AddAsync("zoya", UserRole.Operator);
        await AddAsync("anna", UserRole.Manager);

        Assert.Equal(["admin", "anna", "zoya"], (await _service.GetUsersAsync(_admin)).Select(u => u.Login));
    }

    [Fact]
    public async Task Update_changes_name_role_and_state_and_is_audited_only_when_something_changed()
    {
        await SetUpAdminAsync();
        var op = await AddAsync("ivan", UserRole.Operator);

        await _service.UpdateAsync(_admin, op.Id, op.FullName, op.Role, true);
        var updated = await _service.UpdateAsync(_admin, op.Id, "Иван Иванович", UserRole.Manager, true);

        Assert.Equal(UserRole.Manager, updated.Role);
        Assert.Equal("Иван Иванович", (await _store.GetAsync(op.Id))!.User.FullName);
        Assert.Equal(1, (await ActionsAsync()).Count(a => a == "user_updated"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpdateAsync(_admin, 999, "X", UserRole.Operator, true));
    }

    [Fact]
    public async Task Security_events_are_written_to_the_audit_log_without_passwords()
    {
        await SetUpAdminAsync();
        var op = await AddAsync("ivan", UserRole.Operator);
        await _service.LoginAsync("ivan", "bad-password-text");
        await _service.LoginAsync("ivan", "Temp-Pass-123");
        await _service.ChangePasswordAsync(op.Id, "Temp-Pass-123", "My-Own-Pass-9");
        await _service.RecordLogoutAsync(op);

        Assert.Equal(
            ["user_created", "user_created", "login_failed", "login", "password_changed", "logout"],
            await ActionsAsync());
        var text = string.Join("\n", (await _audit.GetRecentAsync(100)).Select(a => a.Details));
        Assert.DoesNotContain("bad-password-text", text);
        Assert.DoesNotContain("Temp-Pass-123", text);
        Assert.DoesNotContain("My-Own-Pass-9", text);
    }

    [Fact]
    public async Task Lockout_is_audited()
    {
        await SetUpAdminAsync();
        for (var i = 0; i < 3; i++)
        {
            await _service.LoginAsync("admin", "bad");
        }

        await _service.LoginAsync("admin", "Scale-Pass-1");
        var actions = await ActionsAsync();
        Assert.Contains("account_locked", actions);
        Assert.Contains("login_blocked", actions);
    }

    [Fact]
    public async Task Store_survives_reopening_the_database()
    {
        await SetUpAdminAsync();
        var reopened = new SqliteUserStore(new SqliteDatabase(Path.Combine(_dir, "sec.db")));
        var service = new UserService(reopened, new Pbkdf2PasswordHasher(1000), _audit, _time);
        Assert.Equal(LoginStatus.Success, (await service.LoginAsync("admin", "Scale-Pass-1")).Status);
    }

    [Fact]
    public async Task Store_rejects_duplicate_logins_in_any_case_and_unknown_updates()
    {
        await SetUpAdminAsync();
        var dup = new StoredUser(new User { Login = "ADMIN", FullName = "x", CreatedAt = _time.Now }, "h", 0, null);
        await Assert.ThrowsAsync<DuplicateException>(() => _store.InsertAsync(dup));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.UpdateAsync(dup with { User = dup.User with { Id = 12345 } }));
        Assert.Null(await _store.FindByLoginAsync("nobody"));
        Assert.Null(await _store.GetAsync(12345));
    }

    [Fact]
    public async Task Audit_log_returns_newest_first_and_respects_the_limit()
    {
        for (var i = 1; i <= 5; i++)
        {
            await _audit.WriteAsync("u", "a", $"n{i}");
        }

        Assert.Equal(["n5", "n4"], (await _audit.GetRecentAsync(2)).Select(a => a.Details));
    }
}
