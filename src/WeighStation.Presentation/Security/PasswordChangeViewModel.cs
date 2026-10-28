using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Core.Security;

namespace WeighStation.Presentation.Security;

/// <summary>The "change my password" dialog: old password, new password twice.</summary>
public sealed partial class PasswordChangeViewModel(UserService users, CurrentUser current) : ObservableObject
{
    [ObservableProperty] private string _oldPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _errorMessage = "";

    public string UserName => current.DisplayName;

    /// <summary>Raised when the password was changed; the dialog closes on it.</summary>
    public event EventHandler? Completed;

    [RelayCommand]
    private async Task ChangeAsync()
    {
        ErrorMessage = "";
        if (current.User is not { } user)
        {
            ErrorMessage = "Сначала войдите в программу.";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Новый пароль и подтверждение не совпадают.";
            return;
        }

        try
        {
            current.SignIn(await users.ChangePasswordAsync(user.Id, OldPassword, NewPassword));
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (ValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось изменить пароль: {ex.Message}";
        }
    }
}
