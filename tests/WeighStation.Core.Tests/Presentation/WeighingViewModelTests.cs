using WeighStation.Core.Models;
using WeighStation.Presentation.Pages;

namespace WeighStation.Core.Tests.Presentation;

public class WeighingViewModelTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly WeighingViewModel _vm;

    public WeighingViewModelTests() => _vm = _app.NewWeighing();

    public void Dispose()
    {
        _vm.Dispose();
        _app.Dispose();
    }

    [Fact]
    public void Starts_idle_with_green_light()
    {
        Assert.Equal("Платформа свободна", _vm.StateText);
        Assert.False(_vm.IsLightRed);
        Assert.Equal("Зелёный", _vm.LightText);
        Assert.False(_vm.CanCapture);
        Assert.False(_vm.ScaleOnline);
    }

    [Fact]
    public void Shows_weight_plate_and_enables_capture_when_ready()
    {
        _app.SettledTruck();

        Assert.Equal("42 780", _vm.WeightText);
        Assert.Equal("А123ВС 197", _vm.PlateInput);
        Assert.Equal("Авто · 95 %", _vm.PlateHint);
        Assert.True(_vm.IsStable);
        Assert.True(_vm.CanCapture);
        Assert.True(_vm.IsLightRed);
        Assert.True(_vm.FullyOnPlatform);
        Assert.Equal("Вес стабилен — можно фиксировать", _vm.StateText);
        Assert.True(_vm.CaptureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Known_vehicle_fills_the_form_and_the_permitted_mass_gauge()
    {
        await _app.Vehicles.SaveAsync(new Vehicle
        {
            Plate = "А123ВС197", Trailer = "АВ456777", Driver = "Петров С.И.", Company = "Север", Cargo = "Щебень", MaxMassKg = 44000,
        });

        _app.SettledTruck(kg: 42780);
        await TestApp.Eventually(() => _vm.Company == "Север", "form prefill");

        Assert.Equal("Найден в справочнике", _vm.VehicleNote);
        Assert.Equal("АВ4567 77", _vm.Trailer);
        Assert.Equal("Петров С.И.", _vm.Driver);
        Assert.Equal("Щебень", _vm.Cargo);
        Assert.True(_vm.HasMaxMass);
        Assert.Equal(0.972, _vm.LoadFraction, 3);
        Assert.Equal("Запас до предела: 1 220 кг", _vm.ReserveText);
        Assert.False(_vm.IsOverloaded);
    }

    [Fact]
    public async Task Gauge_turns_into_an_overload_warning()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А123ВС197", MaxMassKg = 40000 });

        _app.SettledTruck(kg: 42780);
        await TestApp.Eventually(() => _vm.HasMaxMass, "vehicle lookup");

        Assert.True(_vm.IsOverloaded);
        Assert.Equal(1.0, _vm.LoadFraction);
        Assert.Equal("Превышение: 2 780 кг", _vm.ReserveText);
    }

    [Fact]
    public async Task Unknown_vehicle_is_announced_and_has_no_gauge()
    {
        _app.SettledTruck();
        await TestApp.Eventually(() => _vm.VehicleNote.Length > 0, "vehicle lookup");

        Assert.Equal("Нет в справочнике — будет добавлен после записи", _vm.VehicleNote);
        Assert.False(_vm.HasMaxMass);
        Assert.Equal("", _vm.ReserveText);
    }

    [Fact]
    public async Task Prefill_does_not_overwrite_what_the_operator_typed()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А123ВС197", Company = "Из справочника", Driver = "Иванов" });
        _app.Dev.Sensors(onPlatform: true);
        _vm.Company = "Набрано руками";

        _app.Dev.Plate("А123ВС197");
        await TestApp.Eventually(() => _vm.Driver == "Иванов", "vehicle lookup");

        Assert.Equal("Набрано руками", _vm.Company);
    }

    [Fact]
    public async Task Capture_adds_a_row_reports_net_and_teaches_the_reference_books()
    {
        await _app.RealRepo.AddAsync(
            new Weighing { Time = _app.Time.Now, Plate = "А123ВС197", Kind = WeighingKind.Tare, TareKg = 14320 }, []);
        _app.SettledTruck();
        _vm.Company = "ООО «Север»";
        _vm.Cargo = "Щебень";
        _vm.Driver = "Петров С.И.";

        await _vm.CaptureCommand.ExecuteAsync(null);

        var row = _vm.Recent[0];
        Assert.Equal("А123ВС 197", row.Plate);
        Assert.Equal("28 460", row.Net);
        Assert.Equal("Записано: А123ВС 197 · нетто 28 460 кг", _vm.ResultMessage);
        Assert.False(_vm.ResultIsWarning);
        Assert.Equal("Шлагбаум: открыт", _vm.BarrierText);

        await TestApp.Eventually(() => _vm.CompanyOptions.Contains("ООО «Север»"), "reference learning");
        Assert.Contains("Щебень", _vm.CargoOptions);
        Assert.Contains("Петров С.И.", _vm.DriverOptions);
        var vehicle = (await _app.Vehicles.FindByPlateAsync("А123ВС197"))!;
        Assert.Equal("ООО «Север»", vehicle.Company);
    }

    [Fact]
    public async Task Capture_without_known_tare_warns_that_net_is_missing()
    {
        _app.SettledTruck();
        await _vm.CaptureCommand.ExecuteAsync(null);
        Assert.True(_vm.ResultIsWarning);
        Assert.Equal("Записано: А123ВС 197 · брутто 42 780 кг (тара неизвестна)", _vm.ResultMessage);
    }

    [Fact]
    public async Task Capture_over_the_permitted_mass_is_recorded_but_flagged()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А123ВС197", MaxMassKg = 40000, TareKg = 14000 });
        _app.SettledTruck();
        await TestApp.Eventually(() => _vm.HasMaxMass, "vehicle lookup");

        await _vm.CaptureCommand.ExecuteAsync(null);

        Assert.True(_vm.ResultIsWarning);
        Assert.Contains("превышение допустимой массы на 2 780 кг", _vm.ResultMessage);
        Assert.Single(await _app.RealRepo.GetRecentAsync(10));
    }

    [Fact]
    public async Task Tare_mode_records_tare()
    {
        _vm.IsGross = false;
        _app.SettledTruck(kg: 14300);

        await _vm.CaptureCommand.ExecuteAsync(null);

        Assert.Equal("Записано: А123ВС 197 · тара 14 300 кг", _vm.ResultMessage);
        Assert.Equal(14300, await _app.RealRepo.FindTareKgAsync("А123ВС197"));
    }

    [Fact]
    public async Task Storage_failure_is_shown_and_the_truck_stays_on_the_scale()
    {
        _app.SettledTruck();
        _app.Repo.FailAdd = new IOException("disk full");

        await _vm.CaptureCommand.ExecuteAsync(null);

        Assert.Equal("Не удалось сохранить взвешивание: disk full", _vm.ErrorMessage);
        Assert.Equal("Шлагбаум: закрыт", _vm.BarrierText);
        Assert.True(_vm.CanCapture);

        _app.Repo.FailAdd = null;
        await _vm.CaptureCommand.ExecuteAsync(null);
        Assert.Equal("", _vm.ErrorMessage);
        Assert.Single(_vm.Recent);
    }

    [Fact]
    public void Typing_a_plate_overrides_the_camera_and_is_not_rewritten_while_typing()
    {
        _app.Dev.Sensors(onPlatform: true);
        _app.Dev.Hold(30000, 0, 3);
        Assert.False(_vm.CanCapture);

        _vm.PlateInput = "в777вв 77";

        Assert.True(_vm.CanCapture);
        Assert.Equal("в777вв 77", _vm.PlateInput);
        Assert.Equal("Вручную", _vm.PlateHint);
    }

    [Fact]
    public async Task Typed_details_end_up_in_the_weighing_with_the_operator_name()
    {
        _app.SettledTruck();
        _vm.Company = "Север";
        _vm.Trailer = "ab 4567 77";

        await _vm.CaptureCommand.ExecuteAsync(null);

        var stored = (await _app.RealRepo.GetRecentAsync(1)).Single();
        Assert.Equal("Север", stored.Counterparty);
        Assert.Equal("windows-user", stored.Operator);
        Assert.Equal("ab 4567 77", stored.Trailer);
    }

    [Fact]
    public void Operator_name_from_settings_is_used_once_set()
    {
        _app.Settings.Update(_app.Settings.Current with { OperatorName = "Иванов А.П." });
        Assert.Equal("Иванов А.П.", _app.Settings.OperatorName);
    }

    [Fact]
    public void Form_is_cleared_when_the_platform_becomes_free_again()
    {
        _app.SettledTruck();
        _vm.Company = "Север";
        _app.Dev.Sensors();

        Assert.Equal("", _vm.Company);
        Assert.Equal("", _vm.PlateInput);
        Assert.Equal("Платформа свободна", _vm.StateText);
    }

    [Fact]
    public async Task Previous_result_stays_visible_until_the_next_truck_arrives()
    {
        _app.SettledTruck();
        await _vm.CaptureCommand.ExecuteAsync(null);
        _app.Dev.Sensors();
        Assert.NotEqual("", _vm.ResultMessage);

        _app.Dev.Sensors(entry: true);

        Assert.Equal("", _vm.ResultMessage);
    }

    [Fact]
    public void Cancel_releases_the_truck_without_recording()
    {
        _app.SettledTruck();
        _vm.CancelCommand.Execute(null);
        Assert.Equal("Шлагбаум: открыт", _vm.BarrierText);
        Assert.Empty(_vm.Recent);
    }

    [Fact]
    public void Closing_the_barrier_is_refused_while_a_vehicle_is_on_the_exit_sensor()
    {
        _app.Dev.Sensors(exit: true);
        _vm.OpenBarrierCommand.Execute(null);
        _vm.CloseBarrierCommand.Execute(null);

        Assert.Equal("Нельзя закрыть шлагбаум: на выезде транспорт.", _vm.ErrorMessage);
        Assert.True(_vm.ExitOccupied);
        Assert.Equal("Шлагбаум: открыт", _vm.BarrierText);
    }

    [Fact]
    public void Silent_scale_disables_capture_and_says_so()
    {
        _app.SettledTruck();
        _app.Time.Now += TimeSpan.FromSeconds(10);
        _app.Session.Tick();

        Assert.False(_vm.ScaleOnline);
        Assert.False(_vm.CanCapture);
        Assert.Equal("Нет данных от весов", _vm.StateText);
        Assert.False(_vm.CaptureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Activation_loads_options_and_recent_rows()
    {
        await _app.Companies.SaveAsync(new Company { Name = "Север" });
        await _app.Cargos.SaveAsync(new CargoType { Name = "Песок" });
        await _app.Drivers.SaveAsync(new Driver { FullName = "Сидоров" });
        await _app.AddWeighing();

        await _vm.ActivateAsync();

        Assert.Equal(["Север"], _vm.CompanyOptions);
        Assert.Equal(["Песок"], _vm.CargoOptions);
        Assert.Equal(["Сидоров"], _vm.DriverOptions);
        Assert.Single(_vm.Recent);
    }
}
