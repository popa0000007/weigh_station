using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Core.Security;

namespace WeighStation.Presentation.Security;

public enum LoginMode
{
    /// <summary>Normal login.</summary>
    SignIn,
    /// <summary>No accounts exist yet: create the first administrator.</summary>
    Setup,
    /// <summary>The account has a temporary password: choose a new one before going on.</summary>
    ChangePassword,
}

/// <summary>The window shown before the main window. Signs the user in and tells the application when to go on.</summary>
public sealed partial class LoginViewModel(UserService users, CurrentUser current) : ObservableObject
{
    private User? _pendingUser;
    private string _temporaryPassword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignIn), nameof(IsSetup), nameof(IsChangePassword), nameof(ShowCredentials), nameof(ShowConfirm), nameof(Heading), nameof(SubmitText))]
    private LoginMode _mode = LoginMode.SignIn;

    [ObservableProperty] private string _login = "";
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _errorMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;

    public bool IsSignIn => Mode == LoginMode.SignIn;

    public bool IsSetup => Mode == LoginMode.Setup;

    public bool IsChangePassword => Mode == LoginMode.ChangePassword;

    /// <summary>Login and password fields: shown for sign-in and for creating the first administrator.</summary>
    public bool ShowCredentials => Mode != LoginMode.ChangePassword;

    /// <summary>"Repeat the password" field: shown when a new password is being chosen.</summary>
    public bool ShowConfirm => Mode != LoginMode.SignIn;

    public string Heading => Mode switch
    {
        LoginMode.Setup => "Первый запуск: создайте администратора",
        LoginMode.ChangePassword => "Задайте свой пароль",
        _ => "Вход в АвтоВесы",
    };

    public string SubmitText => Mode switch
    {
        LoginMode.Setup => "Создать и войти",
        LoginMode.ChangePassword => "Сменить пароль и войти",
        _ => "Войти",
    };

    /// <summary>Raised once <see cref="CurrentUser"/> is signed in; the application closes this window and opens the main one.</summary>
    public event EventHandler? Succeeded;

    /// <summary>Decides between normal login and first-run setup. Call once when the window opens.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            Mode = await users.HasUsersAsync() ? LoginMode.SignIn : LoginMode.Setup;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось прочитать список пользователей: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            switch (Mode)
            {
                case LoginMode.Setup:
                    await SetUpAsync();
                    break;
                case LoginMode.ChangePassword:
                    await ChangePasswordAsync();
                    break;
                default:
                    await SignInAsync();
                    break;
            }
        }
        catch (ValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSubmit() => !IsBusy;

    private async Task SetUpAsync()
    {
        if (Password != ConfirmPassword)
        {
            ErrorMessage = "Пароль и подтверждение не совпадают.";
            return;
        }

        var admin = await users.CreateFirstAdministratorAsync(Login, FullName, Password);
        Finish(admin);
    }

    private async Task SignInAsync()
    {
        var result = await users.LoginAsync(Login, Password);
        switch (result.Status)
        {
            case LoginStatus.Success when result.User!.MustChangePassword:
                _pendingUser = result.User;
                _temporaryPassword = Password;
                NewPassword = ConfirmPassword = "";
                Mode = LoginMode.ChangePassword;
                break;
            case LoginStatus.Success:
                Finish(result.User);
                break;
            case LoginStatus.Locked:
                ErrorMessage = $"Учётная запись заблокирована до {result.LockedUntil!.Value.ToLocalTime():HH:mm} из-за неудачных попыток входа.";
                break;
            case LoginStatus.Inactive:
                ErrorMessage = "Учётная запись отключена. Обратитесь к администратору.";
                break;
            default:
                ErrorMessage = "Неверный логин или пароль.";
                break;
        }

        Password = "";
    }

    private async Task ChangePasswordAsync()
    {
        if (_pendingUser is null)
        {
            Mode = LoginMode.SignIn;
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Новый пароль и подтверждение не совпадают.";
            return;
        }

        // The temporary password was just verified; it is asked for again by the service as the "old" one.
        Finish(await users.ChangePasswordAsync(_pendingUser.Id, _temporaryPassword, NewPassword));
    }

    private void Finish(User user)
    {
        _pendingUser = null;
        _temporaryPassword = "";
        Password = NewPassword = ConfirmPassword = "";
        current.SignIn(user);
        Succeeded?.Invoke(this, EventArgs.Empty);
    }
}
