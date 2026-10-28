using WeighStation.Core.Models;

namespace WeighStation.Presentation.Services;

/// <summary>Device events arrive on background threads; anything that touches bindable state goes through this.</summary>
public interface IUiContext
{
    void Post(Action action);
}

/// <summary>Everything that opens a window or touches the user's file system on request. Faked in tests.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message);

    /// <summary>Returns the chosen path, or null if the user cancelled. <paramref name="filter"/> is the Windows "Name|*.ext" form.</summary>
    string? PickSaveFile(string title, string suggestedFileName, string filter);

    void OpenFolder(string path);

    /// <summary>Returns the chosen path, or null if the user cancelled. <paramref name="filter"/> is the Windows "Name|*.ext" form.</summary>
    string? PickOpenFile(string title, string filter);

    /// <summary>Lets the signed-in user change their own password (modal).</summary>
    void ShowChangePassword();
}

public interface ITicketPrinter
{
    void Print(Weighing weighing, IReadOnlyList<AuditEntry> history);
}
