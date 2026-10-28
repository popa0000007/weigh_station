using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Pages;

/// <summary>The operator's main screen: live weight, plate, trip form, capture button.</summary>
public sealed partial class WeighingViewModel : PageViewModel, IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly WeighingSession _session;
    private readonly IWeighingRepository _repository;
    private readonly IVehicleStore _vehicles;
    private readonly IReferenceStore<Company> _companies;
    private readonly IReferenceStore<CargoType> _cargos;
    private readonly IReferenceStore<Driver> _drivers;
    private readonly ReferenceLearner _learner;
    private readonly SettingsService _settings;
    private readonly IUiContext _ui;

    private bool _applyingSnapshot;
    private string _lookedUpPlate = "";
    private SessionState _lastState = SessionState.Idle;

    [ObservableProperty] private string _weightText = "0";
    [ObservableProperty] private bool _isStable;
    [ObservableProperty] private bool _scaleOnline;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _plateInput = "";
    [ObservableProperty] private string _plateHint = "";
    [ObservableProperty] private string _vehicleNote = "";
    [ObservableProperty] private string _trailer = "";
    [ObservableProperty] private string _driver = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _cargo = "";
    [ObservableProperty] private bool _isGross = true;
    [ObservableProperty] private bool _isLightRed;
    [ObservableProperty] private string _lightText = "Зелёный";
    [ObservableProperty] private string _barrierText = "";
    [ObservableProperty] private bool _entryOccupied;
    [ObservableProperty] private bool _fullyOnPlatform;
    [ObservableProperty] private bool _exitOccupied;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private bool _resultIsWarning;

    // Permitted-mass gauge (needs the vehicle in the reference book)
    [ObservableProperty] private bool _hasMaxMass;
    [ObservableProperty] private int _maxMassKg;
    [ObservableProperty] private double _loadFraction;
    [ObservableProperty] private string _reserveText = "";
    [ObservableProperty] private bool _isOverloaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private bool _canCapture;

    public WeighingViewModel(
        WeighingSession session,
        IWeighingRepository repository,
        IVehicleStore vehicles,
        IReferenceStore<Company> companies,
        IReferenceStore<CargoType> cargos,
        IReferenceStore<Driver> drivers,
        ReferenceLearner learner,
        SettingsService settings,
        IUiContext ui)
    {
        _session = session;
        _repository = repository;
        _vehicles = vehicles;
        _companies = companies;
        _cargos = cargos;
        _drivers = drivers;
        _learner = learner;
        _settings = settings;
        _ui = ui;

        _session.Changed += OnSessionChanged;
        _session.WeighingCaptured += OnCaptured;
        _settings.Changed += OnSettingsChanged;
        Apply(_session.Snapshot);
    }

    public override string Title => "Взвешивание";

    public override string IconData => "M3 18h18M5 18l2-9h10l2 9M9 9V6h6v3";

    public ObservableCollection<JournalRow> Recent { get; } = [];

    public ObservableCollection<string> CompanyOptions { get; } = [];

    public ObservableCollection<string> CargoOptions { get; } = [];

    public ObservableCollection<string> DriverOptions { get; } = [];

    public override async Task ActivateAsync()
    {
        try
        {
            ReplaceAll(CompanyOptions, (await _companies.GetAllAsync()).Select(c => c.Name));
            ReplaceAll(CargoOptions, (await _cargos.GetAllAsync()).Select(c => c.Name));
            ReplaceAll(DriverOptions, (await _drivers.GetAllAsync()).Select(d => d.FullName));
            ReplaceAll(Recent, (await _repository.GetRecentAsync(20)).Select(JournalRow.From));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось загрузить данные: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _session.WeighingCaptured -= OnCaptured;
        _settings.Changed -= OnSettingsChanged;
    }

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        ErrorMessage = "";
        try
        {
            await _session.CaptureAsync();
        }
        catch (InvalidOperationException)
        {
            // The state moved on (vehicle left, weight became unstable) between the click and the call.
            ErrorMessage = "Вес больше нельзя зафиксировать: состояние платформы изменилось.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить взвешивание: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        ErrorMessage = "";
        _session.Cancel();
    }

    [RelayCommand]
    private void OpenBarrier() => _session.TrySetBarrier(BarrierPosition.Open);

    [RelayCommand]
    private void CloseBarrier()
    {
        ErrorMessage = _session.TrySetBarrier(BarrierPosition.Closed) ? "" : "Нельзя закрыть шлагбаум: на выезде транспорт.";
    }

    partial void OnPlateInputChanged(string value)
    {
        if (!_applyingSnapshot)
        {
            _session.SetManualPlate(value);
        }
    }

    partial void OnTrailerChanged(string value) => PushDetails();

    partial void OnDriverChanged(string value) => PushDetails();

    partial void OnCompanyChanged(string value) => PushDetails();

    partial void OnCargoChanged(string value) => PushDetails();

    partial void OnIsGrossChanged(bool value) => PushDetails();

    private void PushDetails() => _session.SetDetails(new WeighingDetails
    {
        Trailer = Trailer,
        Driver = Driver,
        Counterparty = Company,
        Cargo = Cargo,
        Kind = IsGross ? WeighingKind.Gross : WeighingKind.Tare,
        Operator = _settings.OperatorName,
    });

    private void OnSettingsChanged(object? sender, EventArgs e) => _ui.Post(PushDetails);

    private void OnSessionChanged(object? sender, SessionSnapshot snapshot) => _ui.Post(() => Apply(snapshot));

    private void OnCaptured(object? sender, Weighing weighing) => _ui.Post(() => ShowCaptured(weighing));

    private void ShowCaptured(Weighing w)
    {
        Recent.Insert(0, JournalRow.From(w));
        if (Recent.Count > 20)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }

        var text = $"Записано: {PlateNumber.Format(w.Plate)} · ";
        text += w.Kind == WeighingKind.Tare
            ? $"тара {DisplayNames.Kg(w.TareKg)} кг"
            : w.NetKg is { } net
                ? $"нетто {DisplayNames.Kg(net)} кг"
                : $"брутто {DisplayNames.Kg(w.GrossKg)} кг (тара неизвестна)";

        ResultIsWarning = false;
        if (HasMaxMass && w.GrossKg is { } gross && gross > MaxMassKg)
        {
            text += $" · превышение допустимой массы на {DisplayNames.Kg(gross - MaxMassKg)} кг";
            ResultIsWarning = true;
        }
        else if (w.Kind == WeighingKind.Gross && w.NetKg is null)
        {
            ResultIsWarning = true;
        }

        ResultMessage = text;
        _ = LearnAsync(w);
    }

    private async Task LearnAsync(Weighing w)
    {
        try
        {
            await _learner.LearnAsync(w);
            await ActivateAsync();
        }
        catch (Exception ex)
        {
            _ui.Post(() => ErrorMessage = $"Взвешивание записано, но справочники не обновились: {ex.Message}");
        }
    }

    private void Apply(SessionSnapshot s)
    {
        WeightText = s.WeightKg.ToString("N0", Ru);
        IsStable = s.IsStable;
        ScaleOnline = s.ScaleOnline;
        StateText = s.Message;
        CanCapture = s.CanCapture;
        IsLightRed = s.Light == TrafficLight.Red;
        LightText = IsLightRed ? "Красный" : "Зелёный";
        BarrierText = s.Barrier == BarrierPosition.Open ? "Шлагбаум: открыт" : "Шлагбаум: закрыт";
        EntryOccupied = s.Platform.EntryOccupied;
        FullyOnPlatform = s.Platform.FullyOnPlatform;
        ExitOccupied = s.Platform.ExitOccupied;
        PlateHint = s.PlateConfidence is { } c ? $"Авто · {c:P0}".Replace(' ', ' ') : (s.Plate is null ? "" : "Вручную");

        // A new vehicle starts: forget the previous result and clear the form.
        if (s.State != _lastState && s.State == SessionState.Entering && _lastState is SessionState.Idle)
        {
            ResultMessage = "";
        }

        _lastState = s.State;

        _applyingSnapshot = true;
        try
        {
            if (s.State == SessionState.Idle)
            {
                Trailer = Driver = Company = Cargo = "";
            }

            // Don't rewrite what the operator is typing (the session normalises it).
            var shown = PlateNumber.Format(s.Plate);
            if (PlateNumber.Normalize(PlateInput) != PlateNumber.Normalize(s.Plate))
            {
                PlateInput = shown;
            }
        }
        finally
        {
            _applyingSnapshot = false;
        }

        var plate = s.Plate ?? "";
        if (plate != _lookedUpPlate)
        {
            _lookedUpPlate = plate;
            ResetVehicle();
            if (plate.Length > 0)
            {
                _ = LookupVehicleAsync(plate);
            }
        }

        UpdateGauge(s.WeightKg);
    }

    private void ResetVehicle()
    {
        VehicleNote = "";
        HasMaxMass = false;
        MaxMassKg = 0;
    }

    private async Task LookupVehicleAsync(string plate)
    {
        Vehicle? vehicle;
        try
        {
            vehicle = await _vehicles.FindByPlateAsync(plate);
        }
        catch (Exception ex)
        {
            _ui.Post(() => ErrorMessage = $"Не удалось прочитать справочник: {ex.Message}");
            return;
        }

        _ui.Post(() =>
        {
            if (plate != _lookedUpPlate)
            {
                return; // another vehicle arrived while we were looking
            }

            if (vehicle is null)
            {
                VehicleNote = "Нет в справочнике — будет добавлен после записи";
                return;
            }

            VehicleNote = "Найден в справочнике";
            if (vehicle.MaxMassKg is { } max)
            {
                HasMaxMass = true;
                MaxMassKg = max;
            }

            // Only fill what the operator hasn't typed.
            Trailer = Fill(Trailer, vehicle.Trailer is null ? null : PlateNumber.Format(vehicle.Trailer));
            Driver = Fill(Driver, vehicle.Driver);
            Company = Fill(Company, vehicle.Company);
            Cargo = Fill(Cargo, vehicle.Cargo);
            UpdateGauge(_session.Snapshot.WeightKg);
        });
    }

    private static string Fill(string current, string? suggestion) =>
        string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(suggestion) ? suggestion : current;

    private void UpdateGauge(int weightKg)
    {
        if (!HasMaxMass || MaxMassKg <= 0)
        {
            LoadFraction = 0;
            ReserveText = "";
            IsOverloaded = false;
            return;
        }

        LoadFraction = Math.Clamp((double)weightKg / MaxMassKg, 0, 1);
        IsOverloaded = weightKg > MaxMassKg;
        ReserveText = IsOverloaded
            ? $"Превышение: {DisplayNames.Kg(weightKg - MaxMassKg)} кг"
            : $"Запас до предела: {DisplayNames.Kg(MaxMassKg - weightKg)} кг";
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
