using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Pages;

public sealed record StatusOption(string Label, WeighingStatus? Value);

/// <summary>Searchable list of all weighings with a detail pane: history, correction, cancellation, export, ticket.</summary>
public sealed partial class JournalViewModel : PageViewModel
{
    private readonly IWeighingRepository _repository;
    private readonly IDialogService _dialogs;
    private readonly ITicketPrinter _printer;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;

    private bool _loadingDetails;
    private Weighing? _selected;
    private IReadOnlyList<AuditEntry> _history = [];

    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private string _plateFilter = "";
    [ObservableProperty] private string _companyFilter = "";
    [ObservableProperty] private StatusOption _statusFilter;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _infoMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand), nameof(CancelWeighingCommand))]
    private JournalRow? _selectedRow;

    // Detail pane
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailTime = "";
    [ObservableProperty] private string _detailGross = "";
    [ObservableProperty] private string _detailTare = "";
    [ObservableProperty] private string _detailNet = "";
    [ObservableProperty] private string _detailStatus = "";
    [ObservableProperty] private string _detailOperator = "";

    [ObservableProperty] private string _editPlate = "";
    [ObservableProperty] private string _editTrailer = "";
    [ObservableProperty] private string _editDriver = "";
    [ObservableProperty] private string _editCompany = "";
    [ObservableProperty] private string _editCargo = "";

    [ObservableProperty] private bool _canEdit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private bool _isDirty;

    public JournalViewModel(
        IWeighingRepository repository,
        IDialogService dialogs,
        ITicketPrinter printer,
        SettingsService settings,
        TimeProvider time)
    {
        _repository = repository;
        _dialogs = dialogs;
        _printer = printer;
        _settings = settings;
        _time = time;

        StatusOptions =
        [
            new("Все", null),
            new("Завершено", WeighingStatus.Completed),
            new("Ручная правка", WeighingStatus.ManualEdit),
            new("Отменено", WeighingStatus.Cancelled),
        ];
        _statusFilter = StatusOptions[0];
        ResetFilterValues();
    }

    public override string Title => "Журнал";

    public override string IconData => "M5 4h14v16H5zM9 9h6M9 13h6M9 17h3";

    public IReadOnlyList<StatusOption> StatusOptions { get; }

    public ObservableCollection<JournalRow> Rows { get; } = [];

    public ObservableCollection<HistoryRow> History { get; } = [];

    public bool HasSelection => SelectedRow is not null;

    public override Task ActivateAsync() => SearchAsync();

    [RelayCommand]
    private async Task SearchAsync()
    {
        ErrorMessage = "";
        var keepId = SelectedRow?.Id;
        try
        {
            var found = await _repository.QueryAsync(BuildQuery());
            Rows.Clear();
            foreach (var w in found)
            {
                Rows.Add(JournalRow.From(w));
            }

            var counted = found.Where(w => w.Status != WeighingStatus.Cancelled).ToList();
            Summary = $"Показано {found.Count} · нетто {DisplayNames.Kg(counted.Sum(w => (long)(w.NetKg ?? 0)))} кг";
            SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось загрузить журнал: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ResetFiltersAsync()
    {
        ResetFilterValues();
        await SearchAsync();
    }

    [RelayCommand]
    private void Export()
    {
        ErrorMessage = InfoMessage = "";
        if (Rows.Count == 0)
        {
            InfoMessage = "Нечего экспортировать: в списке нет записей.";
            return;
        }

        var name = $"journal-{_time.GetLocalNow():yyyy-MM-dd}.csv";
        var path = _dialogs.PickSaveFile("Экспорт журнала", name, "CSV (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            // The BOM makes Excel pick UTF-8 instead of showing garbage for Cyrillic.
            File.WriteAllText(path, JournalExport.ToCsv(Rows.Select(r => r.Source)), new UTF8Encoding(true));
            InfoMessage = $"Сохранено записей: {Rows.Count}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить файл: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Print()
    {
        if (_selected is null)
        {
            return;
        }

        try
        {
            _printer.Print(_selected, _history);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось напечатать талон: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private async Task SaveEditAsync()
    {
        if (_selected is null)
        {
            return;
        }

        ErrorMessage = InfoMessage = "";
        if (PlateNumber.Normalize(EditPlate).Length == 0)
        {
            ErrorMessage = "Госномер не может быть пустым.";
            return;
        }

        var edited = _selected with
        {
            Plate = PlateNumber.Normalize(EditPlate),
            Trailer = Blank(PlateNumber.Normalize(EditTrailer)),
            Driver = Blank(EditDriver),
            Counterparty = Blank(EditCompany),
            Cargo = Blank(EditCargo),
        };

        var changes = DescribeChanges(_selected, edited);
        if (changes.Count == 0)
        {
            IsDirty = false;
            return;
        }

        try
        {
            await _repository.UpdateDescriptionAsync(
                edited, new AuditEntry(_time.GetLocalNow(), _settings.OperatorName, "Правка: " + string.Join("; ", changes)));
            InfoMessage = "Изменения сохранены.";
            await SearchAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить изменения: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CancelWeighingAsync()
    {
        if (_selected is null || _selected.Status == WeighingStatus.Cancelled)
        {
            return;
        }

        if (!_dialogs.Confirm("Отмена взвешивания",
                $"Отменить взвешивание № {_selected.Id} ({PlateNumber.Format(_selected.Plate)})?\n" +
                "Запись останется в журнале, но не попадёт в отчёты."))
        {
            return;
        }

        ErrorMessage = InfoMessage = "";
        try
        {
            await _repository.CancelAsync(
                _selected.Id, new AuditEntry(_time.GetLocalNow(), _settings.OperatorName, "Запись отменена оператором"));
            await SearchAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось отменить запись: {ex.Message}";
        }
    }

    partial void OnSelectedRowChanged(JournalRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        _ = LoadDetailsAsync(value);
    }

    partial void OnEditPlateChanged(string value) => UpdateDirty();

    partial void OnEditTrailerChanged(string value) => UpdateDirty();

    partial void OnEditDriverChanged(string value) => UpdateDirty();

    partial void OnEditCompanyChanged(string value) => UpdateDirty();

    partial void OnEditCargoChanged(string value) => UpdateDirty();

    private async Task LoadDetailsAsync(JournalRow? row)
    {
        _loadingDetails = true;
        try
        {
            _selected = row?.Source;
            History.Clear();
            _history = [];

            if (row is null)
            {
                DetailTitle = DetailTime = DetailGross = DetailTare = DetailNet = DetailStatus = DetailOperator = "";
                EditPlate = EditTrailer = EditDriver = EditCompany = EditCargo = "";
                CanEdit = false;
                IsDirty = false;
                return;
            }

            var w = row.Source;
            DetailTitle = $"Взвешивание № {w.Id}";
            DetailTime = DisplayNames.DateTime(w.Time);
            DetailGross = DisplayNames.Kg(w.GrossKg);
            DetailTare = DisplayNames.Kg(w.TareKg);
            DetailNet = DisplayNames.Kg(w.NetKg);
            DetailStatus = DisplayNames.Of(w.Status);
            DetailOperator = w.Operator ?? "";
            EditPlate = PlateNumber.Format(w.Plate);
            EditTrailer = PlateNumber.Format(w.Trailer);
            EditDriver = w.Driver ?? "";
            EditCompany = w.Counterparty ?? "";
            EditCargo = w.Cargo ?? "";
            CanEdit = w.Status != WeighingStatus.Cancelled;
            IsDirty = false;
        }
        finally
        {
            _loadingDetails = false;
        }

        if (row is null)
        {
            return;
        }

        try
        {
            var history = await _repository.GetHistoryAsync(row.Id);
            if (SelectedRow?.Id != row.Id)
            {
                return; // the user already moved on
            }

            _history = history;
            History.Clear();
            foreach (var entry in history)
            {
                History.Add(HistoryRow.From(entry));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось загрузить историю: {ex.Message}";
        }
    }

    private void UpdateDirty()
    {
        if (_loadingDetails || _selected is null)
        {
            return;
        }

        IsDirty = CanEdit && (
            PlateNumber.Normalize(EditPlate) != _selected.Plate
            || PlateNumber.Normalize(EditTrailer) != (_selected.Trailer ?? "")
            || EditDriver.Trim() != (_selected.Driver ?? "")
            || EditCompany.Trim() != (_selected.Counterparty ?? "")
            || EditCargo.Trim() != (_selected.Cargo ?? ""));
    }

    private WeighingQuery BuildQuery() => new()
    {
        From = DateFrom is { } from ? new DateTimeOffset(from.Date, _time.LocalTimeZone.GetUtcOffset(from.Date)) : null,
        To = DateTo is { } to ? new DateTimeOffset(to.Date.AddDays(1), _time.LocalTimeZone.GetUtcOffset(to.Date.AddDays(1))) : null,
        Plate = PlateFilter,
        Company = CompanyFilter,
        Status = StatusFilter.Value,
    };

    private void ResetFilterValues()
    {
        var today = _time.GetLocalNow().Date;
        DateFrom = today;
        DateTo = today;
        PlateFilter = CompanyFilter = "";
        StatusFilter = StatusOptions[0];
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string> DescribeChanges(Weighing before, Weighing after)
    {
        var changes = new List<string>();
        Add("Номер", PlateNumber.Format(before.Plate), PlateNumber.Format(after.Plate));
        Add("Прицеп", PlateNumber.Format(before.Trailer), PlateNumber.Format(after.Trailer));
        Add("Водитель", before.Driver, after.Driver);
        Add("Контрагент", before.Counterparty, after.Counterparty);
        Add("Груз", before.Cargo, after.Cargo);
        return changes;

        void Add(string field, string? was, string? now)
        {
            if ((was ?? "") != (now ?? ""))
            {
                changes.Add($"{field}: «{was}» → «{now}»");
            }
        }
    }
}
