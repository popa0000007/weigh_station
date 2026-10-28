using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using WeighStation.Core.Security;
using WeighStation.Presentation.Security;
using WeighStation.Presentation.Services;

namespace WeighStation.App.Services;

public sealed class WpfDialogService(UserService users, CurrentUser current) : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public string? PickSaveFile(string title, string suggestedFileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedFileName,
            Filter = filter,
            OverwritePrompt = true,
            AddExtension = true,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public void ShowChangePassword() =>
        new ChangePasswordWindow(new PasswordChangeViewModel(users, current)) { Owner = Application.Current.MainWindow }.ShowDialog();

    public void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
}
