using WeighStation.Core.Models;
using WeighStation.Presentation.Pages;

namespace WeighStation.Core.Tests.Presentation;

public class ReferencesViewModelTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly ReferencesViewModel _vm;

    public ReferencesViewModelTests() => _vm = _app.NewReferences();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Activation_loads_all_four_books()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77" });
        await _app.Companies.SaveAsync(new Company { Name = "Север" });
        await _app.Cargos.SaveAsync(new CargoType { Name = "Щебень" });
        await _app.Drivers.SaveAsync(new Driver { FullName = "Петров" });

        await _vm.ActivateAsync();

        Assert.Single(_vm.Vehicles.Items);
        Assert.Single(_vm.Companies.Items);
        Assert.Single(_vm.Cargos.Items);
        Assert.Single(_vm.Drivers.Items);
    }

    [Fact]
    public async Task New_vehicle_is_saved_selected_and_listed()
    {
        await _vm.ActivateAsync();
        var vehicles = _vm.Vehicles;
        vehicles.NewCommand.Execute(null);
        Assert.True(vehicles.IsNew);

        vehicles.Plate = "a 123 bc 197";
        vehicles.Trailer = "ab 4567 77";
        vehicles.Company = "Север";
        vehicles.TareKg = "14 320";
        vehicles.MaxMassKg = "44000";
        await vehicles.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Сохранено.", vehicles.InfoMessage);
        var saved = Assert.IsType<Vehicle>(vehicles.SelectedItem);
        Assert.Equal("А123ВС197", saved.Plate);
        Assert.Equal("АВ456777", saved.Trailer);
        Assert.Equal(14320, saved.TareKg);
        Assert.Equal(44000, saved.MaxMassKg);
        Assert.False(vehicles.IsNew);
        Assert.Equal("А123ВС 197", vehicles.Plate);
        Assert.Single(vehicles.Items);
    }

    [Fact]
    public async Task Vehicle_form_validation_messages()
    {
        await _vm.ActivateAsync();
        var v = _vm.Vehicles;

        await v.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Укажите госномер.", v.ErrorMessage);

        v.Plate = "А111АА77";
        v.TareKg = "много";
        await v.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Тара: введите массу в килограммах (например, 44000).", v.ErrorMessage);

        v.TareKg = "";
        v.MaxMassKg = "0";
        await v.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("Допустимая масса:", v.ErrorMessage);

        Assert.Empty(await _app.Vehicles.GetAllAsync());
    }

    [Fact]
    public async Task Duplicate_plate_is_reported_not_thrown()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77" });
        await _vm.ActivateAsync();
        _vm.Vehicles.NewCommand.Execute(null);
        _vm.Vehicles.Plate = "a111aa 77";

        await _vm.Vehicles.SaveCommand.ExecuteAsync(null);

        Assert.Contains("уже есть в справочнике", _vm.Vehicles.ErrorMessage);
        Assert.Single(await _app.Vehicles.GetAllAsync());
    }

    [Fact]
    public async Task Selecting_loads_the_form_and_saving_updates_in_place()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77", Driver = "Старый", MaxMassKg = 40000 });
        await _vm.ActivateAsync();
        var v = _vm.Vehicles;

        v.SelectedItem = v.Items[0];
        Assert.Equal("Старый", v.Driver);
        Assert.Equal("40000", v.MaxMassKg);

        v.Driver = "Новый";
        await v.SaveCommand.ExecuteAsync(null);

        var only = Assert.Single(await _app.Vehicles.GetAllAsync());
        Assert.Equal("Новый", only.Driver);
        Assert.Equal(40000, only.MaxMassKg);
    }

    [Fact]
    public async Task Search_filters_by_plate_company_or_driver_in_any_spelling()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77", Company = "Север" });
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "В222ВВ77", Driver = "Петров" });
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "Е333ЕЕ77" });
        await _vm.ActivateAsync();

        _vm.Vehicles.Search = "a111";
        Assert.Equal(["А111АА77"], _vm.Vehicles.Items.Select(i => i.Plate));
        _vm.Vehicles.Search = "север";
        Assert.Equal(["А111АА77"], _vm.Vehicles.Items.Select(i => i.Plate));
        _vm.Vehicles.Search = "петр";
        Assert.Equal(["В222ВВ77"], _vm.Vehicles.Items.Select(i => i.Plate));
        _vm.Vehicles.Search = "";
        Assert.Equal(3, _vm.Vehicles.Items.Count);
    }

    [Fact]
    public async Task Delete_needs_confirmation()
    {
        var id = await _app.Cargos.SaveAsync(new CargoType { Name = "Щебень" });
        await _vm.ActivateAsync();
        _vm.Cargos.SelectedItem = _vm.Cargos.Items[0];

        _app.Dialogs.ConfirmAnswer = false;
        await _vm.Cargos.DeleteCommand.ExecuteAsync(null);
        Assert.Single(await _app.Cargos.GetAllAsync());

        _app.Dialogs.ConfirmAnswer = true;
        await _vm.Cargos.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(await _app.Cargos.GetAllAsync());
        Assert.Empty(_vm.Cargos.Items);
        Assert.Equal("", _vm.Cargos.Name);
        Assert.Equal("Удалено.", _vm.Cargos.InfoMessage);
        Assert.Contains("Щебень", _app.Dialogs.Questions.Last());
        Assert.True(id > 0);
    }

    [Fact]
    public async Task Delete_is_unavailable_without_a_selection()
    {
        await _vm.ActivateAsync();
        Assert.False(_vm.Drivers.DeleteCommand.CanExecute(null));
        await _app.Drivers.SaveAsync(new Driver { FullName = "Петров" });
        await _vm.ActivateAsync();
        _vm.Drivers.SelectedItem = _vm.Drivers.Items[0];
        Assert.True(_vm.Drivers.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Company_driver_and_cargo_forms_validate_and_save()
    {
        await _vm.ActivateAsync();

        await _vm.Companies.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Укажите название.", _vm.Companies.ErrorMessage);
        _vm.Companies.Name = " ООО «Север» ";
        _vm.Companies.Inn = "7700000000";
        await _vm.Companies.SaveCommand.ExecuteAsync(null);
        Assert.Equal("ООО «Север»", (await _app.Companies.GetAllAsync()).Single().Name);

        await _vm.Drivers.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Укажите ФИО.", _vm.Drivers.ErrorMessage);
        _vm.Drivers.FullName = "Петров С.И.";
        _vm.Drivers.Phone = "+7 900 000-00-00";
        await _vm.Drivers.SaveCommand.ExecuteAsync(null);
        Assert.Equal("+7 900 000-00-00", (await _app.Drivers.GetAllAsync()).Single().Phone);

        _vm.Cargos.Name = "Песок";
        await _vm.Cargos.SaveCommand.ExecuteAsync(null);
        _vm.Cargos.NewCommand.Execute(null);
        _vm.Cargos.Name = "песок";
        await _vm.Cargos.SaveCommand.ExecuteAsync(null);
        Assert.Contains("уже есть", _vm.Cargos.ErrorMessage);
    }

    [Fact]
    public async Task New_clears_selection_and_form()
    {
        await _app.Companies.SaveAsync(new Company { Name = "Север", Inn = "1" });
        await _vm.ActivateAsync();
        _vm.Companies.SelectedItem = _vm.Companies.Items[0];
        Assert.Equal("1", _vm.Companies.Inn);

        _vm.Companies.NewCommand.Execute(null);

        Assert.Null(_vm.Companies.SelectedItem);
        Assert.Equal("", _vm.Companies.Name);
        Assert.Equal("", _vm.Companies.Inn);
        Assert.True(_vm.Companies.IsNew);
    }
}
