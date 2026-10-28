using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Presentation.Pages;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation;

public sealed record DeviceChip(string Name, bool IsOnline, string Tooltip);

/// <summary>
/// The main window: the left navigation rail, the current page, the device chips in the status bar and the clock.
/// Knows nothing about what the pages do - it only switches between them and tells them when they are shown.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly WeighingSession _session;
    private readonly IDeviceMonitor _monitor;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly IUiContext _ui;

    [ObservableProperty] private PageViewModel _selectedPage;
    [ObservableProperty] private string _clockText = "";

    public ShellViewModel(
        IReadOnlyList<PageViewModel> pages,
        WeighingSession session,
        IDeviceMonitor monitor,
        SettingsService settings,
        TimeProvider time,
        IUiContext ui)
    {
        Pages = pages;
        _session = session;
        _monitor = monitor;
        _settings = settings;
        _time = time;
        _ui = ui;
        _selectedPage = pages[0];

        _monitor.StatusChanged += OnStatusChanged;
        _settings.Changed += OnSettingsChanged;
        RefreshChips();
        UpdateClock();
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public ObservableCollection<DeviceChip> DeviceChips { get; } = [];

    public string OperatorName => _settings.OperatorName;

    /// <summary>Opens the first page; call once after the window is shown.</summary>
    public Task StartAsync() => SelectedPage.ActivateAsync();

    /// <summary>Once a second: clock, and the session's check that the scale is still talking.</summary>
    public void Tick()
    {
        UpdateClock();
        _session.Tick();
    }

    public void Dispose()
    {
        _monitor.StatusChanged -= OnStatusChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    partial void OnSelectedPageChanged(PageViewModel value) => _ = ActivateSafelyAsync(value);

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

    private void OnStatusChanged(object? sender, EventArgs e) => _ui.Post(RefreshChips);

    private void OnSettingsChanged(object? sender, EventArgs e) => _ui.Post(() => OnPropertyChanged(nameof(OperatorName)));

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
