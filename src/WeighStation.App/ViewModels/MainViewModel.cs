using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.App.ViewModels;

public sealed record WeighingRow(string Time, string Plate, string Counterparty, string Cargo, string Gross, string Tare, string Net);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly WeighingSession _session;
    private readonly IWeighingRepository _repository;
    private readonly string _operator;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private bool _applyingSnapshot;

    [ObservableProperty] private string _weightText = "0";
    [ObservableProperty] private bool _isStable;
    [ObservableProperty] private string _stateText = "Платформа свободна";
    [ObservableProperty] private string _plateInput = "";
    [ObservableProperty] private string _plateHint = "";
    [ObservableProperty] private string _trailer = "";
    [ObservableProperty] private string _driver = "";
    [ObservableProperty] private string _counterparty = "";
    [ObservableProperty] private string _cargo = "";
    [ObservableProperty] private bool _isGross = true;
    [ObservableProperty] private bool _isLightRed;
    [ObservableProperty] private string _lightText = "Зелёный";
    [ObservableProperty] private string _barrierText = "Шлагбаум: закрыт";
    [ObservableProperty] private string _errorMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private bool _canCapture;

    public MainViewModel(WeighingSession session, IWeighingRepository repository, string operatorName)
    {
        _session = session;
        _repository = repository;
        _operator = operatorName;

        _session.Changed += OnSessionChanged;
        _session.WeighingCaptured += OnCaptured;
        Apply(_session.Snapshot);
        _ = LoadRecentAsync();
    }

    public ObservableCollection<WeighingRow> Recent { get; } = [];

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _session.WeighingCaptured -= OnCaptured;
    }

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        ErrorMessage = "";
        try
        {
            await _session.CaptureAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить взвешивание: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => _session.Cancel();

    [RelayCommand]
    private void OpenBarrier() => _session.TrySetBarrier(BarrierPosition.Open);

    [RelayCommand]
    private void CloseBarrier()
    {
        if (!_session.TrySetBarrier(BarrierPosition.Closed))
        {
            ErrorMessage = "Нельзя закрыть шлагбаум: на выезде транспорт.";
        }
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
    partial void OnCounterpartyChanged(string value) => PushDetails();
    partial void OnCargoChanged(string value) => PushDetails();
    partial void OnIsGrossChanged(bool value) => PushDetails();

    private void PushDetails() => _session.SetDetails(new WeighingDetails
    {
        Trailer = Trailer,
        Driver = Driver,
        Counterparty = Counterparty,
        Cargo = Cargo,
        Kind = IsGross ? WeighingKind.Gross : WeighingKind.Tare,
        Operator = _operator,
    });

    private void OnSessionChanged(object? sender, SessionSnapshot snapshot) =>
        _ui.BeginInvoke(() => Apply(snapshot));

    private void OnCaptured(object? sender, Weighing weighing) =>
        _ui.BeginInvoke(() => Recent.Insert(0, ToRow(weighing)));

    private void Apply(SessionSnapshot s)
    {
        WeightText = s.WeightKg.ToString("N0", Ru);
        IsStable = s.IsStable;
        StateText = s.Message;
        CanCapture = s.CanCapture;
        IsLightRed = s.Light == TrafficLight.Red;
        LightText = IsLightRed ? "Красный" : "Зелёный";
        BarrierText = s.Barrier == BarrierPosition.Open ? "Шлагбаум: открыт" : "Шлагбаум: закрыт";
        PlateHint = s.PlateConfidence is { } c ? $"Авто · {c:P0}" : (s.Plate is null ? "" : "Вручную");

        _applyingSnapshot = true;
        try
        {
            if (s.State == SessionState.Idle)
            {
                Trailer = Driver = Counterparty = Cargo = "";
            }

            // Don't rewrite what the operator is typing (the session upper-cases and trims it).
            var plate = s.Plate ?? "";
            if (!string.Equals(plate, PlateInput.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                PlateInput = plate;
            }
        }
        finally
        {
            _applyingSnapshot = false;
        }
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            foreach (var w in await _repository.GetRecentAsync(20))
            {
                Recent.Add(ToRow(w));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось загрузить журнал: {ex.Message}";
        }
    }

    private static WeighingRow ToRow(Weighing w) => new(
        w.Time.ToString("HH:mm", Ru),
        w.Plate,
        w.Counterparty ?? "",
        w.Cargo ?? "",
        Kg(w.GrossKg),
        Kg(w.TareKg),
        Kg(w.NetKg));

    private static string Kg(int? value) => value is null ? "—" : value.Value.ToString("N0", Ru);
}
