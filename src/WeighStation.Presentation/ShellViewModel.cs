using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Security;
using WeighStation.Presentation.Pages;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation;

public sealed record DeviceChip(string Name, bool IsOnline, string Tooltip);

/// <summary>
/// The main window: the left navigation rail, the current page, the device chips in the status bar, the clock and the
/// signed-in user. Knows nothing about what the pages do - it shows only the pages the user may open and tells them when
/// they are shown.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly IReadOnlyList<PageViewModel> _allPages;
    private readonly WeighingSession _session;
    private readonly IDeviceMonitor _monitor;
    private readonly CurrentUser _user;
    private readonly UserService _users;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly IUiContext _ui;

    private bool _started;

    [ObservableProperty] private PageViewModel? _selectedPage;
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _userText = "";

    public ShellViewModel(
        IReadOnlyList<PageViewModel> pages,
        WeighingSession session,
        IDeviceMonitor monitor,
        CurrentUser user,
        UserService users,
        IDialogService dialogs,
        TimeProvider time,
        IUiContext ui)
    {
        _allPages = pages;
        _session = session;
        _monitor = monitor;
        _user = user;
        _users = users;
        _dialogs = dialogs;
        _time = time;
        _ui = ui;

        _monitor.StatusChanged += OnStatusChanged;
        _user.Changed += OnUserChanged;
        RefreshChips();
        UpdateClock();
        ApplyUser();
    }

    /// <summary>The pages the signed-in user may open, in navigation order.</summary>
    public ObservableCollection<PageViewModel> Pages { get; } = [];

    public ObservableCollection<DeviceChip> DeviceChips { get; } = [];

    /// <summary>Raised after a sign-out: the application shows the login window again.</summary>
    public event EventHandler? LoggedOut;

    /// <summary>Opens the first page; call once after the window is shown.</summary>
    public Task StartAsync()
    {
        _started = true;
        return SelectedPage?.ActivateAsync() ?? Task.CompletedTask;
    }

    /// <summary>Once a second: clock, and the session's check that the scale is still talking.</summary>
    public void Tick()
    {
        UpdateClock();
        _session.Tick();
    }

    public void Dispose()
    {
        _monitor.StatusChanged -= OnStatusChanged;
        _user.Changed -= OnUserChanged;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (_user.User is { } user)
        {
            try
            {
                await _users.RecordLogoutAsync(user);
            }
            catch
            {
                // Signing out must not fail because the log could not be written.
            }
        }

        _user.SignOut();
        LoggedOut?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ChangePassword() => _dialogs.ShowChangePassword();

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        if (value is not null && _started)
        {
            _ = ActivateSafelyAsync(value);
        }
    }

    private async Task ActivateSafelyAsync(PageViewModel page)
    {
        try
        {
            await page.ActivateAsync();
        }
        catch
        {
            // Pages report their own load errors; a failure here must not take the shell down.
        }
    }

    private void OnUserChanged(object? sender, EventArgs e) => _ui.Post(ApplyUser);

    /// <summary>Shows only the pages the current user may open, and keeps the selection if it is still allowed.</summary>
    private void ApplyUser()
    {
        var allowed = _allPages.Where(p => _user.Can(p.Required)).ToList();
        var keep = SelectedPage;

        Pages.Clear();
        foreach (var page in allowed)
        {
            Pages.Add(page);
        }

        SelectedPage = keep is not null && allowed.Contains(keep) ? keep : allowed.FirstOrDefault();
        UserText = _user.User is { } u ? $"{u.DisplayName} · {RolePermissions.Name(u.Role)}" : "Не выполнен вход";
    }

    private void OnStatusChanged(object? sender, EventArgs e) => _ui.Post(RefreshChips);

    private void RefreshChips()
    {
        DeviceChips.Clear();
        foreach (var d in _monitor.GetStatuses())
        {
            DeviceChips.Add(new DeviceChip(d.Name, d.Health == DeviceHealth.Online, $"{d.Name}: {d.Detail}"));
        }
    }

    private void UpdateClock() => ClockText = _time.GetLocalNow().ToString("dd.MM.yyyy  HH:mm:ss", Ru);
}
