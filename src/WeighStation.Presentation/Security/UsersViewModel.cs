using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Core.Security;
using WeighStation.Presentation.Pages;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Security;

public sealed record RoleOption(UserRole Role, string Name, string Description);

public sealed record UserRow(long Id, string Login, string FullName, string RoleName, string Status, string LastLogin, User Source);

public sealed record AuditRow(string Time, string User, string Action, string Details);

/// <summary>Administrator's page: accounts, roles, temporary passwords, unlocking, and the security log.</summary>
public sealed partial class UsersViewModel : PageViewModel
{
    private readonly UserService _users;
    private readonly IAuditLog _audit;
    private readonly CurrentUser _current;
    private readonly IDialogService _dialogs;
    private IReadOnlyDictionary<long, DateTimeOffset> _locks = new Dictionary<long, DateTimeOffset>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetPasswordCommand), nameof(UnlockCommand))]
    private UserRow? _selectedUser;

    [ObservableProperty] private string _login = "";
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private RoleOption _selectedRole;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _infoMessage = "";

    public UsersViewModel(UserService users, IAuditLog audit, CurrentUser current, IDialogService dialogs)
    {
        _users = users;
        _audit = audit;
        _current = current;
        _dialogs = dialogs;
        RoleOptions = Enum.GetValues<UserRole>()
            .Select(r => new RoleOption(r, RolePermissions.Name(r), RolePermissions.Describe(r))).ToList();
        _selectedRole = RoleOptions[0];
    }

    public override string Title => "Пользователи";

    public override string IconData => "M12 12 A4 4 0 1 0 12 4 A4 4 0 1 0 12 12 Z M4 21 V19 A4 4 0 0 1 8 15 H16 A4 4 0 0 1 20 19 V21";

    public override Permission Required => Permission.ManageUsers;

    public IReadOnlyList<RoleOption> RoleOptions { get; }

    public ObservableCollection<UserRow> Users { get; } = [];

    public ObservableCollection<AuditRow> SecurityLog { get; } = [];

    /// <summary>True while the form shows a new account (the login can only be chosen then).</summary>
    public bool IsNew => SelectedUser is null;

    public bool IsExisting => SelectedUser is not null;

    public override async Task ActivateAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось загрузить пользователей: {ex.Message}";
        }
    }

    [RelayCommand]
    private void New()
    {
        ErrorMessage = InfoMessage = "";
        SelectedUser = null;
        LoadForm(null);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = InfoMessage = "";
        if (!TryActor(out var actor))
        {
            return;
        }

        try
        {
            if (SelectedUser is null)
            {
                var created = await _users.CreateAsync(actor, Login, FullName, SelectedRole.Role, Password);
                await LoadAsync(created.Id);
                InfoMessage = $"Пользователь «{created.Login}» создан. Временный пароль нужно сменить при первом входе.";
            }
            else
            {
                var updated = await _users.UpdateAsync(actor, SelectedUser.Id, FullName, SelectedRole.Role, IsActive);
                if (updated.Id == actor.Id)
                {
                    _current.SignIn(updated); // changing yourself takes effect immediately
                }

                await LoadAsync(updated.Id);
                InfoMessage = "Сохранено.";
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or UnauthorizedAccessException or DuplicateException)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsExisting))]
    private async Task ResetPasswordAsync()
    {
        ErrorMessage = InfoMessage = "";
        if (SelectedUser is null || !TryActor(out var actor))
        {
            return;
        }

        if (!_dialogs.Confirm("Сброс пароля", $"Задать «{SelectedUser.Login}» новый временный пароль? Пользователь должен будет сменить его при входе."))
        {
            return;
        }

        try
        {
            await _users.ResetPasswordAsync(actor, SelectedUser.Id, Password);
            Password = "";
            await LoadAsync(SelectedUser.Id);
            InfoMessage = "Временный пароль задан.";
        }
        catch (Exception ex) when (ex is ValidationException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сбросить пароль: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsExisting))]
    private async Task UnlockAsync()
    {
        ErrorMessage = InfoMessage = "";
        if (SelectedUser is null || !TryActor(out var actor))
        {
            return;
        }

        try
        {
            await _users.UnlockAsync(actor, SelectedUser.Id);
            await LoadAsync(SelectedUser.Id);
            InfoMessage = "Учётная запись разблокирована.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось разблокировать: {ex.Message}";
        }
    }

    partial void OnSelectedUserChanged(UserRow? value)
    {
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IsExisting));
        if (value is not null)
        {
            ErrorMessage = InfoMessage = "";
            LoadForm(value.Source);
        }
    }

    private bool TryActor(out User actor)
    {
        if (_current.User is { } user && _current.Can(Permission.ManageUsers))
        {
            actor = user;
            return true;
        }

        actor = null!;
        ErrorMessage = "Недостаточно прав для управления пользователями.";
        return false;
    }

    private async Task LoadAsync(long? select = null)
    {
        if (!TryActor(out var actor))
        {
            return;
        }

        var users = await _users.GetUsersAsync(actor);
        _locks = await _users.GetLocksAsync(actor);
        var keep = select ?? SelectedUser?.Id;

        Users.Clear();
        foreach (var u in users)
        {
            Users.Add(ToRow(u));
        }

        SelectedUser = keep is { } id ? Users.FirstOrDefault(r => r.Id == id) : null;
        if (SelectedUser is null)
        {
            LoadForm(null);
        }

        SecurityLog.Clear();
        foreach (var a in await _audit.GetRecentAsync(200))
        {
            SecurityLog.Add(new AuditRow(a.Time.ToLocalTime().ToString("dd.MM HH:mm:ss"), a.User, ActionName(a.Action), a.Details));
        }
    }

    private UserRow ToRow(User u)
    {
        var status = !u.IsActive ? "Отключён"
            : _locks.TryGetValue(u.Id, out var until) ? $"Заблокирован до {until.ToLocalTime():HH:mm}"
            : u.MustChangePassword ? "Ждёт смены пароля"
            : "Активен";
        return new UserRow(
            u.Id, u.Login, u.FullName, RolePermissions.Name(u.Role), status,
            u.LastLoginAt is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "—", u);
    }

    private void LoadForm(User? u)
    {
        Login = u?.Login ?? "";
        FullName = u?.FullName ?? "";
        SelectedRole = RoleOptions.First(r => r.Role == (u?.Role ?? UserRole.Operator));
        IsActive = u?.IsActive ?? true;
        Password = "";
    }

    private static string ActionName(string action) => action switch
    {
        "login" => "Вход",
        "logout" => "Выход",
        "login_failed" => "Неудачный вход",
        "login_blocked" => "Вход в заблокированную запись",
        "account_locked" => "Блокировка записи",
        "user_created" => "Создан пользователь",
        "user_updated" => "Изменён пользователь",
        "user_unlocked" => "Разблокировка",
        "password_changed" => "Смена пароля",
        "password_change_failed" => "Неудачная смена пароля",
        "password_reset" => "Сброс пароля",
        "import" => "Импорт данных",
        _ => action,
    };
}
