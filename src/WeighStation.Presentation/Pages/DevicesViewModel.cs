using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Pages;

public sealed record DeviceRow(string Name, bool IsOnline, string StatusText, string Detail);

/// <summary>Equipment status, live signals, manual control and connection settings.</summary>
public sealed partial class DevicesViewModel : PageViewModel, IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly WeighingSession _session;
    private readonly IDeviceMonitor _monitor;
    private readonly ISimulationControl? _simulation;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IUiContext _ui;
    private bool _syncingSimulation;

    // Live signals
    [ObservableProperty] private string _weightText = "0";
    [ObservableProperty] private bool _scaleStable;
    [ObservableProperty] private bool _entryOccupied;
    [ObservableProperty] private bool _platformOccupied;
    [ObservableProperty] private bool _exitOccupied;
    [ObservableProperty] private string _lightText = "";
    [ObservableProperty] private bool _lightIsRed;
    [ObservableProperty] private string _barrierText = "";
    [ObservableProperty] private string _stateText = "";

    // Simulation (only when the simulator drives the app)
    [ObservableProperty] private bool _simAutoTrucks;
    [ObservableProperty] private bool _simScaleConnected;
    [ObservableProperty] private bool _simCamerasConnected;

    // Connection settings
    [ObservableProperty] private string _plcHost = "";
    [ObservableProperty] private string _plcRack = "";
    [ObservableProperty] private string _plcSlot = "";
    [ObservableProperty] private string _scaleHost = "";
    [ObservableProperty] private string _scalePort = "";
    [ObservableProperty] private string _camera1Url = "";
    [ObservableProperty] private string _camera2Url = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _infoMessage = "";

    public DevicesViewModel(
        WeighingSession session,
        IDeviceMonitor monitor,
        ISimulationControl? simulation,
        SettingsService settings,
        IDialogService dialogs,
        IUiContext ui)
    {
        _session = session;
        _monitor = monitor;
        _simulation = simulation;
        _settings = settings;
        _dialogs = dialogs;
        _ui = ui;

        _session.Changed += OnSessionChanged;
        _monitor.StatusChanged += OnStatusChanged;
        if (_simulation is not null)
        {
            _simulation.Changed += OnSimulationChanged;
        }

        RefreshDevices();
        Apply(_session.Snapshot);
        SyncSimulation();
        LoadSettings(_settings.Current.Devices);
    }

    public override string Title => "Устройства";

    public override string IconData => "M4 6h16v10H4zM8 20h8M12 16v4";

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public bool IsSimulation => _simulation is not null;

    public override Task ActivateAsync()
    {
        LoadSettings(_settings.Current.Devices);
        ErrorMessage = InfoMessage = "";
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _monitor.StatusChanged -= OnStatusChanged;
        if (_simulation is not null)
        {
            _simulation.Changed -= OnSimulationChanged;
        }
    }

    [RelayCommand]
    private void OpenBarrier()
    {
        if (_dialogs.Confirm("Шлагбаум", "Открыть шлагбаум вручную?"))
        {
            _session.TrySetBarrier(BarrierPosition.Open);
        }
    }

    [RelayCommand]
    private void CloseBarrier()
    {
        ErrorMessage = "";
        if (!_session.TrySetBarrier(BarrierPosition.Closed))
        {
            ErrorMessage = "Нельзя закрыть шлагбаум: на выезде транспорт.";
        }
    }

    [RelayCommand]
    private void SaveConnection()
    {
        ErrorMessage = InfoMessage = "";
        if (!int.TryParse(PlcRack, NumberStyles.None, CultureInfo.InvariantCulture, out var rack) || rack > 7
            || !int.TryParse(PlcSlot, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot > 31)
        {
            ErrorMessage = "Rack ПЛК — число от 0 до 7, Slot — от 0 до 31.";
            return;
        }

        if (!int.TryParse(ScalePort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            ErrorMessage = "Порт терминала — число от 1 до 65535.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PlcHost) || string.IsNullOrWhiteSpace(ScaleHost))
        {
            ErrorMessage = "Укажите адреса ПЛК и терминала.";
            return;
        }

        try
        {
            _settings.Update(_settings.Current with
            {
                Devices = new DeviceSettings
                {
                    PlcHost = PlcHost.Trim(),
                    PlcRack = rack,
                    PlcSlot = slot,
                    ScaleHost = ScaleHost.Trim(),
                    ScalePort = port,
                    Camera1Url = Camera1Url.Trim(),
                    Camera2Url = Camera2Url.Trim(),
                },
            });
            InfoMessage = "Параметры подключения сохранены. Они будут использованы после подключения драйверов оборудования.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить: {ex.Message}";
        }
    }

    partial void OnSimAutoTrucksChanged(bool value) => PushSimulation(s => s.AutoTrucks = value);

    partial void OnSimScaleConnectedChanged(bool value) => PushSimulation(s => s.ScaleConnected = value);

    partial void OnSimCamerasConnectedChanged(bool value) => PushSimulation(s => s.CamerasConnected = value);

    private void PushSimulation(Action<ISimulationControl> change)
    {
        if (_simulation is not null && !_syncingSimulation)
        {
            change(_simulation);
        }
    }

    private void SyncSimulation()
    {
        if (_simulation is null)
        {
            return;
        }

        _syncingSimulation = true;
        try
        {
            SimAutoTrucks = _simulation.AutoTrucks;
            SimScaleConnected = _simulation.ScaleConnected;
            SimCamerasConnected = _simulation.CamerasConnected;
        }
        finally
        {
            _syncingSimulation = false;
        }
    }

    private void OnSessionChanged(object? sender, SessionSnapshot s) => _ui.Post(() => Apply(s));

    private void OnStatusChanged(object? sender, EventArgs e) => _ui.Post(RefreshDevices);

    private void OnSimulationChanged(object? sender, EventArgs e) => _ui.Post(SyncSimulation);

    private void Apply(SessionSnapshot s)
    {
        WeightText = s.WeightKg.ToString("N0", Ru);
        ScaleStable = s.IsStable;
        EntryOccupied = s.Platform.EntryOccupied;
        PlatformOccupied = s.Platform.FullyOnPlatform;
        ExitOccupied = s.Platform.ExitOccupied;
        LightIsRed = s.Light == TrafficLight.Red;
        LightText = LightIsRed ? "Красный" : "Зелёный";
        BarrierText = s.Barrier == BarrierPosition.Open ? "Открыт" : "Закрыт";
        StateText = s.Message;
    }

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var d in _monitor.GetStatuses())
        {
            Devices.Add(new DeviceRow(
                d.Name, d.Health == DeviceHealth.Online, d.Health == DeviceHealth.Online ? "В сети" : "Нет связи", d.Detail));
        }
    }

    private void LoadSettings(DeviceSettings d)
    {
        PlcHost = d.PlcHost;
        PlcRack = d.PlcRack.ToString(CultureInfo.InvariantCulture);
        PlcSlot = d.PlcSlot.ToString(CultureInfo.InvariantCulture);
        ScaleHost = d.ScaleHost;
        ScalePort = d.ScalePort.ToString(CultureInfo.InvariantCulture);
        Camera1Url = d.Camera1Url;
        Camera2Url = d.Camera2Url;
    }
}
