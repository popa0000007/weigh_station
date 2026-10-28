using WeighStation.Core.Models;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Presentation;
using WeighStation.Presentation.Pages;

namespace WeighStation.Core.Tests.Presentation;

public class ReportsViewModelTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly ReportsViewModel _vm;

    public ReportsViewModelTests() => _vm = _app.NewReports();

    public void Dispose() => _app.Dispose();

    private static DateTimeOffset Day(int day) => new(2026, 10, day, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Period_defaults_to_the_current_month()
    {
        Assert.Equal(new DateTime(2026, 10, 1), _vm.DateFrom);
        Assert.Equal(new DateTime(2026, 10, 28), _vm.DateTo);
    }

    [Fact]
    public async Task Builds_rows_totals_and_relative_bar_lengths()
    {
        await _app.AddWeighing(company: "Север", net: 30000, time: Day(3));
        await _app.AddWeighing(company: "Север", net: 10000, time: Day(4));
        await _app.AddWeighing(company: "Юг", net: 20000, time: Day(5));
        await _app.AddWeighing(company: "Юг", net: 99999, status: WeighingStatus.Cancelled, time: Day(5));
        await _app.AddWeighing(company: "Старый", net: 5000, time: new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

        await _vm.ActivateAsync();

        Assert.Equal(["Север", "Юг"], _vm.Rows.Select(r => r.Key));
        Assert.Equal("2", _vm.Rows[0].Weighings);
        Assert.Equal("40 000", _vm.Rows[0].Net);
        Assert.Equal("20 000", _vm.Rows[0].Average);
        Assert.Equal(1.0, _vm.Rows[0].Share);
        Assert.Equal(0.5, _vm.Rows[1].Share);
        Assert.Equal("Взвешиваний: 3 · нетто: 60 000 кг", _vm.Totals);
        Assert.Equal("", _vm.Warning);
    }

    [Fact]
    public async Task Grouping_can_be_changed()
    {
        await _app.AddWeighing("А111АА77", cargo: "Щебень", time: Day(3));
        await _app.AddWeighing("А111АА77", cargo: "Песок", time: Day(4));

        _vm.Grouping = _vm.GroupingOptions.Single(g => g.Value == ReportGrouping.Vehicle);
        await _vm.BuildCommand.ExecuteAsync(null);
        Assert.Equal("А111АА 77", Assert.Single(_vm.Rows).Key);

        _vm.Grouping = _vm.GroupingOptions.Single(g => g.Value == ReportGrouping.Day);
        await _vm.BuildCommand.ExecuteAsync(null);
        Assert.Equal(["03.10.2026", "04.10.2026"], _vm.Rows.Select(r => r.Key));
    }

    [Fact]
    public async Task Warns_about_weighings_without_net()
    {
        await _app.RealRepo.AddAsync(
            new Weighing { Time = Day(3), Plate = "А111АА77", Kind = WeighingKind.Gross, GrossKg = 40000 }, []);

        await _vm.ActivateAsync();

        Assert.Contains("нет нетто", _vm.Warning);
    }

    [Fact]
    public async Task Reversed_period_is_rejected()
    {
        _vm.DateFrom = new DateTime(2026, 10, 20);
        _vm.DateTo = new DateTime(2026, 10, 10);
        await _vm.BuildCommand.ExecuteAsync(null);
        Assert.Equal("Дата «с» позже даты «по».", _vm.ErrorMessage);
        Assert.Empty(_vm.Rows);
    }

    [Fact]
    public async Task Empty_period_gives_empty_report_and_disables_export()
    {
        await _vm.ActivateAsync();
        Assert.Empty(_vm.Rows);
        Assert.False(_vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_writes_csv_with_totals()
    {
        await _app.AddWeighing(company: "Север", net: 30000, time: Day(3));
        await _vm.ActivateAsync();
        Assert.True(_vm.ExportCommand.CanExecute(null));
        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "report.csv");

        _vm.ExportCommand.Execute(null);

        var text = File.ReadAllText(_app.Dialogs.SavePath);
        Assert.Contains("Группа;Взвешиваний;Нетто, кг;Среднее нетто, кг", text);
        Assert.Contains("Север;1;30000;30000", text);
        Assert.Contains("Итого;1;30000;", text);
        Assert.Equal("Отчёт сохранён.", _vm.InfoMessage);
    }
}

public class DevicesViewModelTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Lists_devices_and_follows_their_status()
    {
        using var vm = _app.NewDevices();
        Assert.Equal(["ПЛК", "Терминал весов", "Камера 1", "Камера 2"], vm.Devices.Select(d => d.Name));
        Assert.All(vm.Devices, d => Assert.True(d.IsOnline));

        _app.Sim.ScaleConnected = false;

        var scale = vm.Devices.Single(d => d.Name == "Терминал весов");
        Assert.False(scale.IsOnline);
        Assert.Equal("Нет связи", scale.StatusText);
        Assert.False(vm.SimScaleConnected);
    }

    [Fact]
    public void Shows_live_signals_from_the_session()
    {
        using var vm = _app.NewDevices();
        _app.Dev.Sensors(entry: true);
        Assert.True(vm.EntryOccupied);
        Assert.False(vm.PlatformOccupied);

        _app.SettledTruck();

        Assert.Equal("42 780", vm.WeightText);
        Assert.True(vm.ScaleStable);
        Assert.True(vm.PlatformOccupied);
        Assert.Equal("Красный", vm.LightText);
        Assert.Equal("Закрыт", vm.BarrierText);
    }

    [Fact]
    public void Simulation_switches_drive_the_simulator()
    {
        using var vm = _app.NewDevices();
        Assert.True(vm.IsSimulation);

        vm.SimCamerasConnected = false;
        vm.SimAutoTrucks = false;

        Assert.False(_app.Sim.CamerasConnected);
        Assert.False(_app.Sim.AutoTrucks);
        Assert.True(_app.Sim.ScaleConnected);
    }

    [Fact]
    public void Without_a_simulator_the_simulation_panel_is_off()
    {
        using var vm = _app.NewDevices(simulation: false);
        Assert.False(vm.IsSimulation);
        vm.SimScaleConnected = false;
        Assert.True(_app.Sim.ScaleConnected);
    }

    [Fact]
    public void Manual_barrier_opening_needs_confirmation()
    {
        using var vm = _app.NewDevices();

        _app.Dialogs.ConfirmAnswer = false;
        vm.OpenBarrierCommand.Execute(null);
        Assert.Equal(BarrierPosition.Closed, _app.Dev.Barrier);

        _app.Dialogs.ConfirmAnswer = true;
        vm.OpenBarrierCommand.Execute(null);
        Assert.Equal(BarrierPosition.Open, _app.Dev.Barrier);
        Assert.Equal("Открыт", vm.BarrierText);
    }

    [Fact]
    public void Closing_is_refused_while_the_exit_sensor_is_occupied()
    {
        using var vm = _app.NewDevices();
        _app.Dev.Sensors(exit: true);
        _app.Dialogs.ConfirmAnswer = true;
        vm.OpenBarrierCommand.Execute(null);

        vm.CloseBarrierCommand.Execute(null);

        Assert.Equal("Нельзя закрыть шлагбаум: на выезде транспорт.", vm.ErrorMessage);
        Assert.Equal(BarrierPosition.Open, _app.Dev.Barrier);
    }

    [Fact]
    public void Connection_settings_are_validated_and_persisted()
    {
        using var vm = _app.NewDevices();
        Assert.Equal("192.168.0.10", vm.PlcHost);

        vm.PlcHost = "10.0.0.5";
        vm.PlcRack = "0";
        vm.PlcSlot = "2";
        vm.ScaleHost = "10.0.0.6";
        vm.ScalePort = "4001";
        vm.Camera1Url = "rtsp://10.0.0.7/a";
        vm.SaveConnectionCommand.Execute(null);

        Assert.Equal("", vm.ErrorMessage);
        var saved = _app.SettingsStore.Saved.Devices;
        Assert.Equal("10.0.0.5", saved.PlcHost);
        Assert.Equal(2, saved.PlcSlot);
        Assert.Equal(4001, saved.ScalePort);
        Assert.Equal("rtsp://10.0.0.7/a", saved.Camera1Url);
    }

    [Theory]
    [InlineData("rack", "9")]
    [InlineData("slot", "40")]
    [InlineData("port", "0")]
    [InlineData("port", "70000")]
    [InlineData("port", "abc")]
    [InlineData("host", "")]
    public void Bad_connection_values_are_rejected_and_nothing_is_saved(string field, string value)
    {
        using var vm = _app.NewDevices();
        switch (field)
        {
            case "rack": vm.PlcRack = value; break;
            case "slot": vm.PlcSlot = value; break;
            case "port": vm.ScalePort = value; break;
            case "host": vm.PlcHost = value; break;
        }

        vm.SaveConnectionCommand.Execute(null);

        Assert.NotEqual("", vm.ErrorMessage);
        Assert.Equal(new DeviceSettings(), _app.SettingsStore.Saved.Devices);
    }

    [Fact]
    public void Storage_error_while_saving_settings_is_reported()
    {
        using var vm = _app.NewDevices();
        _app.SettingsStore.FailWith = new IOException("read-only disk");
        vm.SaveConnectionCommand.Execute(null);
        Assert.Equal("Не удалось сохранить: read-only disk", vm.ErrorMessage);
    }
}

public class SettingsViewModelTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests() => _vm = _app.NewSettings();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Shows_current_values()
    {
        Assert.Equal("500", _vm.MinWeightKg);
        Assert.Equal("20", _vm.ToleranceKg);
        Assert.Equal("2", _vm.WindowSeconds);
        Assert.Equal("80", _vm.MinConfidencePercent);
        Assert.True(_vm.RequireTerminalStable);
        Assert.False(_vm.AutoCapture);
        Assert.Equal("5", _vm.AutoCaptureSeconds);
        Assert.Equal("3", _vm.ScaleTimeoutSeconds);
        Assert.Equal(Path.Combine(_app.Dir, "app.db"), _vm.DatabasePath);
    }

    [Fact]
    public void Saving_applies_to_the_running_session_and_persists()
    {
        _vm.MinWeightKg = "1000";
        _vm.WindowSeconds = "1,5";
        _vm.MinConfidencePercent = "90";
        _vm.AutoCapture = true;
        _vm.AutoCaptureSeconds = "10";
        _vm.OperatorName = " Иванов А.П. ";

        _vm.SaveCommand.Execute(null);

        Assert.Equal("", _vm.ErrorMessage);
        Assert.Equal("Настройки сохранены и применены.", _vm.InfoMessage);
        var o = _app.Session.Options;
        Assert.Equal(1000, o.MinWeightKg);
        Assert.Equal(TimeSpan.FromSeconds(1.5), o.StabilityWindow);
        Assert.Equal(0.9, o.MinPlateConfidence, 3);
        Assert.True(o.AutoCapture);
        Assert.Equal(TimeSpan.FromSeconds(10), o.AutoCaptureDelay);
        Assert.Equal("Иванов А.П.", _app.Settings.OperatorName);
        Assert.Equal(o, _app.SettingsStore.Saved.Weighing);
    }

    [Theory]
    [InlineData("min", "-5")]
    [InlineData("min", "abc")]
    [InlineData("min", "200000")]
    [InlineData("tol", "0")]
    [InlineData("window", "0")]
    [InlineData("window", "100")]
    [InlineData("conf", "0")]
    [InlineData("conf", "101")]
    [InlineData("auto", "0")]
    [InlineData("timeout", "")]
    public void Out_of_range_values_are_rejected_and_nothing_changes(string field, string value)
    {
        switch (field)
        {
            case "min": _vm.MinWeightKg = value; break;
            case "tol": _vm.ToleranceKg = value; break;
            case "window": _vm.WindowSeconds = value; break;
            case "conf": _vm.MinConfidencePercent = value; break;
            case "auto": _vm.AutoCaptureSeconds = value; break;
            case "timeout": _vm.ScaleTimeoutSeconds = value; break;
        }

        _vm.SaveCommand.Execute(null);

        Assert.NotEqual("", _vm.ErrorMessage);
        Assert.Equal(new WeighingOptions(), _app.Session.Options);
        Assert.Equal(new AppSettings(), _app.SettingsStore.Saved);
    }

    [Fact]
    public async Task Reset_defaults_fills_the_form_but_applies_only_on_save()
    {
        _vm.MinWeightKg = "1000";
        _vm.SaveCommand.Execute(null);
        Assert.Equal(1000, _app.Session.Options.MinWeightKg);

        _vm.ResetDefaultsCommand.Execute(null);
        Assert.Equal("500", _vm.MinWeightKg);
        Assert.Equal(1000, _app.Session.Options.MinWeightKg);

        _vm.SaveCommand.Execute(null);
        Assert.Equal(500, _app.Session.Options.MinWeightKg);

        _vm.MinWeightKg = "777";
        await _vm.ActivateAsync();
        Assert.Equal("500", _vm.MinWeightKg);
    }

    [Fact]
    public async Task Backup_writes_a_consistent_copy_that_contains_the_data()
    {
        await _app.AddWeighing("А123ВС197");
        await _app.Companies.SaveAsync(new Company { Name = "Север" });
        var target = Path.Combine(_app.Dir, "backup", "copy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "old junk");
        _app.Dialogs.SavePath = target;

        await _vm.BackupCommand.ExecuteAsync(null);

        Assert.Equal($"Копия сохранена: {target}", _vm.InfoMessage);
        var copy = new SqliteDatabase(target);
        Assert.Equal("А123ВС197", (await new SqliteWeighingRepository(copy).GetRecentAsync(5)).Single().Plate);
        Assert.Equal("Север", (await new SqliteCompanyStore(copy).GetAllAsync()).Single().Name);
        Assert.Matches(@"weighings-2026-10-28-1430\.db", _app.Dialogs.SaveSuggestions.Single());
    }

    [Fact]
    public async Task Backup_cancelled_in_the_dialog_does_nothing()
    {
        _app.Dialogs.SavePath = null;
        await _vm.BackupCommand.ExecuteAsync(null);
        Assert.Equal("", _vm.InfoMessage);
        Assert.Equal("", _vm.ErrorMessage);
    }

    [Fact]
    public void Open_data_folder_asks_the_shell_to_open_the_database_directory()
    {
        _vm.OpenDataFolderCommand.Execute(null);
        Assert.Equal(_app.Dir, _app.Dialogs.OpenedFolders.Single());
    }
}

public class ShellViewModelTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private sealed class SpyPage(string title) : PageViewModel
    {
        public int Activations { get; private set; }
        public override string Title => title;
        public override string IconData => "M0 0";

        public override Task ActivateAsync()
        {
            Activations++;
            return Task.CompletedTask;
        }
    }

    private ShellViewModel Shell(params PageViewModel[] pages) =>
        new(pages, _app.Session, _app.Sim, _app.Settings, _app.Time, _app.Ui);

    [Fact]
    public async Task First_page_is_selected_and_activated_on_start_then_each_switch_activates_the_new_page()
    {
        var a = new SpyPage("A");
        var b = new SpyPage("B");
        using var shell = Shell(a, b);
        Assert.Same(a, shell.SelectedPage);

        await shell.StartAsync();
        Assert.Equal(1, a.Activations);

        shell.SelectedPage = b;
        await TestApp.Eventually(() => b.Activations == 1, "page activation");
        Assert.Equal(1, a.Activations);

        shell.SelectedPage = a;
        await TestApp.Eventually(() => a.Activations == 2, "re-activation");
    }

    [Fact]
    public void Device_chips_follow_status_changes()
    {
        using var shell = Shell(new SpyPage("A"));
        Assert.Equal(4, shell.DeviceChips.Count);
        Assert.All(shell.DeviceChips, c => Assert.True(c.IsOnline));

        _app.Sim.CamerasConnected = false;

        Assert.Equal(2, shell.DeviceChips.Count(c => !c.IsOnline));
        Assert.Contains("Нет связи", shell.DeviceChips.First(c => c.Name == "Камера 1").Tooltip);
    }

    [Fact]
    public void Tick_updates_the_clock_and_lets_the_session_notice_a_dead_scale()
    {
        using var shell = Shell(new SpyPage("A"));
        Assert.Equal("28.10.2026  14:30:00", shell.ClockText);

        _app.SettledTruck();
        Assert.True(_app.Session.Snapshot.CanCapture);

        _app.Time.Now += TimeSpan.FromSeconds(10);
        shell.Tick();

        Assert.Equal("28.10.2026  14:30:10", shell.ClockText);
        Assert.False(_app.Session.Snapshot.CanCapture);
    }

    [Fact]
    public void Operator_name_follows_the_settings()
    {
        using var shell = Shell(new SpyPage("A"));
        Assert.Equal("windows-user", shell.OperatorName);
        _app.Settings.Update(_app.Settings.Current with { OperatorName = "Иванов" });
        Assert.Equal("Иванов", shell.OperatorName);
    }

    [Fact]
    public async Task A_page_that_throws_on_activation_does_not_break_the_shell()
    {
        var bad = new ThrowingPage();
        using var shell = Shell(new SpyPage("A"), bad);
        shell.SelectedPage = bad;
        await Task.Delay(50);
        Assert.Same(bad, shell.SelectedPage);
    }

    private sealed class ThrowingPage : PageViewModel
    {
        public override string Title => "Bad";
        public override string IconData => "M0 0";
        public override Task ActivateAsync() => throw new InvalidOperationException("boom");
    }
}
