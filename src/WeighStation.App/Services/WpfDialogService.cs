using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using WeighStation.Presentation.Services;

namespace WeighStation.App.Services;

public sealed class WpfDialogService : IDialogService
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

    public void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
}
