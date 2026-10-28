using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Core.Security;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Pages;

/// <summary>
/// List + edit form for one reference book. The four books only differ in their fields, so the list/search/save/delete
/// logic is written once here; subclasses say how to fill the form from an item and build an item from the form.
/// </summary>
public abstract partial class ReferenceEditorViewModel<T> : ObservableObject where T : class
{
    private readonly IReferenceStore<T> _store;
    private readonly IDialogService _dialogs;
    private readonly CurrentUser _user;
    private List<T> _all = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _infoMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private T? _selectedItem;

    protected ReferenceEditorViewModel(IReferenceStore<T> store, IDialogService dialogs, CurrentUser user)
    {
        _store = store;
        _dialogs = dialogs;
        _user = user;
        _user.Changed += (_, _) => RefreshPermissions();
    }

    /// <summary>The form is read-only for users who may look at the books but not change them.</summary>
    public bool CanModify => _user.Can(Permission.EditReferences);

    public abstract string Title { get; }

    public ObservableCollection<T> Items { get; } = [];

    /// <summary>True while the form shows a new, not yet saved record.</summary>
    public bool IsNew => SelectedItem is null;

    protected abstract long IdOf(T item);

    protected abstract string DeleteQuestion(T item);

    protected abstract bool Matches(T item, string needle);

    /// <summary>Fills the form from the item, or clears it for a new record (null).</summary>
    protected abstract void LoadForm(T? item);

    /// <summary>Builds the record from the form. Returns an error text instead if the form is invalid.</summary>
    protected abstract (T? Item, string? Error) ReadForm(long id);

    public async Task LoadAsync()
    {
        try
        {
            _all = [.. await _store.GetAllAsync()];
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось загрузить справочник: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void New()
    {
        ErrorMessage = InfoMessage = "";
        SelectedItem = null;
        LoadForm(null);
        OnPropertyChanged(nameof(IsNew));
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task SaveAsync()
    {
        ErrorMessage = InfoMessage = "";
        if (!CanModify)
        {
            ErrorMessage = "Недостаточно прав для изменения справочников.";
            return;
        }

        var id = SelectedItem is null ? 0 : IdOf(SelectedItem);
        var (item, error) = ReadForm(id);
        if (item is null)
        {
            ErrorMessage = error ?? "Проверьте заполнение формы.";
            return;
        }

        try
        {
            var savedId = await _store.SaveAsync(item);
            _all = [.. await _store.GetAllAsync()];
            ApplyFilter();
            SelectedItem = Items.FirstOrDefault(x => IdOf(x) == savedId) ?? _all.FirstOrDefault(x => IdOf(x) == savedId);
            InfoMessage = "Сохранено.";
        }
        catch (DuplicateException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedItem is null || !CanModify || !_dialogs.Confirm("Удаление", DeleteQuestion(SelectedItem)))
        {
            return;
        }

        ErrorMessage = InfoMessage = "";
        try
        {
            await _store.DeleteAsync(IdOf(SelectedItem));
            SelectedItem = null;
            LoadForm(null);
            await LoadAsync();
            InfoMessage = "Удалено.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось удалить: {ex.Message}";
        }
    }

    private bool CanDelete() => SelectedItem is not null && CanModify;

    private void RefreshPermissions()
    {
        OnPropertyChanged(nameof(CanModify));
        NewCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnSelectedItemChanged(T? value)
    {
        OnPropertyChanged(nameof(IsNew));
        if (value is not null)
        {
            ErrorMessage = InfoMessage = "";
            LoadForm(value);
        }
    }

    private void ApplyFilter()
    {
        var keep = SelectedItem is null ? (long?)null : IdOf(SelectedItem);
        var needle = Search.Trim();
        Items.Clear();
        foreach (var item in _all.Where(x => needle.Length == 0 || Matches(x, needle)))
        {
            Items.Add(item);
        }

        if (keep is { } id && Items.FirstOrDefault(x => IdOf(x) == id) is { } same)
        {
            SelectedItem = same;
        }
    }

    protected static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ?? false;

    protected static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    protected static (int? Value, string? Error) ParseKg(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var kg) && kg is > 0 and < 1_000_000
            ? (kg, null)
            : (null, $"{field}: введите массу в килограммах (например, 44000).");
    }
}

public sealed partial class VehiclesEditorViewModel(IVehicleStore store, IDialogService dialogs, CurrentUser user)
    : ReferenceEditorViewModel<Vehicle>(store, dialogs, user)
{
    [ObservableProperty] private string _plate = "";
    [ObservableProperty] private string _trailer = "";
    [ObservableProperty] private string _driver = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _cargo = "";
    [ObservableProperty] private string _tareKg = "";
    [ObservableProperty] private string _maxMassKg = "";
    [ObservableProperty] private string _note = "";

    public override string Title => "Транспорт";

    protected override long IdOf(Vehicle item) => item.Id;

    protected override string DeleteQuestion(Vehicle item) => $"Удалить транспорт {PlateNumber.Format(item.Plate)} из справочника?";

    protected override bool Matches(Vehicle v, string needle) =>
        PlateNumber.Normalize(v.Plate).Contains(PlateNumber.Normalize(needle), StringComparison.Ordinal)
        || Contains(v.Company, needle) || Contains(v.Driver, needle);

    protected override void LoadForm(Vehicle? v)
    {
        Plate = v is null ? "" : PlateNumber.Format(v.Plate);
        Trailer = v is null ? "" : PlateNumber.Format(v.Trailer);
        Driver = v?.Driver ?? "";
        Company = v?.Company ?? "";
        Cargo = v?.Cargo ?? "";
        TareKg = v?.TareKg?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaxMassKg = v?.MaxMassKg?.ToString(CultureInfo.InvariantCulture) ?? "";
        Note = v?.Note ?? "";
    }

    protected override (Vehicle? Item, string? Error) ReadForm(long id)
    {
        if (PlateNumber.Normalize(Plate).Length == 0)
        {
            return (null, "Укажите госномер.");
        }

        var (tare, tareError) = ParseKg(TareKg, "Тара");
        var (max, maxError) = ParseKg(MaxMassKg, "Допустимая масса");
        var problem = tareError ?? maxError;
        if (problem is not null)
        {
            return (null, problem);
        }

        return (new Vehicle
        {
            Id = id,
            Plate = PlateNumber.Normalize(Plate),
            Trailer = Blank(PlateNumber.Normalize(Trailer)),
            Driver = Blank(Driver),
            Company = Blank(Company),
            Cargo = Blank(Cargo),
            TareKg = tare,
            MaxMassKg = max,
            Note = Blank(Note),
        }, null);
    }
}

public sealed partial class CompaniesEditorViewModel(IReferenceStore<Company> store, IDialogService dialogs, CurrentUser user)
    : ReferenceEditorViewModel<Company>(store, dialogs, user)
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _inn = "";
    [ObservableProperty] private string _note = "";

    public override string Title => "Контрагенты";

    protected override long IdOf(Company item) => item.Id;

    protected override string DeleteQuestion(Company item) => $"Удалить контрагента «{item.Name}» из справочника?";

    protected override bool Matches(Company c, string needle) => Contains(c.Name, needle) || Contains(c.Inn, needle);

    protected override void LoadForm(Company? c)
    {
        Name = c?.Name ?? "";
        Inn = c?.Inn ?? "";
        Note = c?.Note ?? "";
    }

    protected override (Company? Item, string? Error) ReadForm(long id) =>
        string.IsNullOrWhiteSpace(Name)
            ? (null, "Укажите название.")
            : (new Company { Id = id, Name = Name.Trim(), Inn = Blank(Inn), Note = Blank(Note) }, null);
}

public sealed partial class CargosEditorViewModel(IReferenceStore<CargoType> store, IDialogService dialogs, CurrentUser user)
    : ReferenceEditorViewModel<CargoType>(store, dialogs, user)
{
    [ObservableProperty] private string _name = "";

    public override string Title => "Грузы";

    protected override long IdOf(CargoType item) => item.Id;

    protected override string DeleteQuestion(CargoType item) => $"Удалить груз «{item.Name}» из справочника?";

    protected override bool Matches(CargoType c, string needle) => Contains(c.Name, needle);

    protected override void LoadForm(CargoType? c) => Name = c?.Name ?? "";

    protected override (CargoType? Item, string? Error) ReadForm(long id) =>
        string.IsNullOrWhiteSpace(Name) ? (null, "Укажите название.") : (new CargoType { Id = id, Name = Name.Trim() }, null);
}

public sealed partial class DriversEditorViewModel(IReferenceStore<Driver> store, IDialogService dialogs, CurrentUser user)
    : ReferenceEditorViewModel<Driver>(store, dialogs, user)
{
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _phone = "";

    public override string Title => "Водители";

    protected override long IdOf(Driver item) => item.Id;

    protected override string DeleteQuestion(Driver item) => $"Удалить водителя «{item.FullName}» из справочника?";

    protected override bool Matches(Driver d, string needle) => Contains(d.FullName, needle) || Contains(d.Phone, needle);

    protected override void LoadForm(Driver? d)
    {
        FullName = d?.FullName ?? "";
        Phone = d?.Phone ?? "";
    }

    protected override (Driver? Item, string? Error) ReadForm(long id) =>
        string.IsNullOrWhiteSpace(FullName)
            ? (null, "Укажите ФИО.")
            : (new Driver { Id = id, FullName = FullName.Trim(), Phone = Blank(Phone) }, null);
}

/// <summary>The "Reference books" page: four editors side by side in tabs.</summary>
public sealed class ReferencesViewModel : PageViewModel
{
    public ReferencesViewModel(
        IVehicleStore vehicles,
        IReferenceStore<Company> companies,
        IReferenceStore<CargoType> cargos,
        IReferenceStore<Driver> drivers,
        IDialogService dialogs,
        CurrentUser user)
    {
        Vehicles = new VehiclesEditorViewModel(vehicles, dialogs, user);
        Companies = new CompaniesEditorViewModel(companies, dialogs, user);
        Cargos = new CargosEditorViewModel(cargos, dialogs, user);
        Drivers = new DriversEditorViewModel(drivers, dialogs, user);
    }

    public override string Title => "Справочники";

    public override string IconData => "M4 5h16M4 12h16M4 19h16";

    public override Permission Required => Permission.ViewReferences;

    public VehiclesEditorViewModel Vehicles { get; }

    public CompaniesEditorViewModel Companies { get; }

    public CargosEditorViewModel Cargos { get; }

    public DriversEditorViewModel Drivers { get; }

    public override async Task ActivateAsync() =>
        await Task.WhenAll(Vehicles.LoadAsync(), Companies.LoadAsync(), Cargos.LoadAsync(), Drivers.LoadAsync());
}
