using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Import;
using WeighStation.Core.Security;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Pages;

public sealed record KindOption(ImportKind Kind, string Title);

public sealed record MassUnitOption(MassUnit Unit, string Title);

/// <summary>One entry of a "which column?" drop-down. Index -1 means "don't import this field".</summary>
public sealed record ColumnOption(int Index, string Name);

public sealed record PreviewRow(int Row, string Action, string Message, string Summary, bool IsError, bool IsWarning);

/// <summary>One line of the mapping table: a field the program knows, and the column of the file that feeds it.</summary>
public sealed partial class FieldMappingViewModel(ImportField field, IReadOnlyList<ColumnOption> options, ColumnOption selected, Action changed)
    : ObservableObject
{
    [ObservableProperty] private ColumnOption _selected = selected;

    public string Key => field.Key;

    public string Title => field.Required ? field.Title + " *" : field.Title;

    public string Hint => field.Hint.Length == 0 ? "" : $"например, {field.Hint}";

    public bool Required => field.Required;

    public IReadOnlyList<ColumnOption> Options => options;

    partial void OnSelectedChanged(ColumnOption value) => changed();
}

/// <summary>
/// Migration of customers' data: pick a file, check how its columns were recognised, look at the preview with problems
/// marked, then import. Nothing is written until the button is pressed, and the write is all-or-nothing.
/// </summary>
public sealed partial class ImportViewModel : PageViewModel
{
    private const int MaxPreview = 500;

    private readonly ImportService _import;
    private readonly ITableReader _reader;
    private readonly IDialogService _dialogs;
    private readonly CurrentUser _user;
    private readonly TimeProvider _time;
    private TableData? _table;
    private ImportPlan? _plan;
    private int _version;
    private bool _rebuilding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindHelp))]
    private KindOption _selectedKind;

    [ObservableProperty] private MassUnitOption _selectedMassUnit;
    [ObservableProperty] private bool _updateExisting;
    [ObservableProperty] private bool _showProblemsOnly;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _previewNote = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _infoMessage = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private int _progressMax = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(ChooseFileCommand), nameof(SaveProblemsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(SaveProblemsCommand))]
    private bool _hasPlan;

    public ImportViewModel(ImportService import, ITableReader reader, IDialogService dialogs, CurrentUser user, TimeProvider time)
    {
        _import = import;
        _reader = reader;
        _dialogs = dialogs;
        _user = user;
        _time = time;
        _user.Changed += (_, _) =>
        {
            ImportCommand.NotifyCanExecuteChanged();
            ChooseFileCommand.NotifyCanExecuteChanged();
        };

        KindOptions = Enum.GetValues<ImportKind>().Select(k => new KindOption(k, ImportSchema.Title(k))).ToList();
        MassUnitOptions = [new(MassUnit.Kilograms, "Килограммы"), new(MassUnit.Tonnes, "Тонны")];
        _selectedKind = KindOptions[0];
        _selectedMassUnit = MassUnitOptions[0];
    }

    public override string Title => "Импорт";

    public override string IconData => "M12 3 V15 M7 10 L12 15 L17 10 M4 19 H20";

    public override Permission Required => Permission.ImportData;

    public IReadOnlyList<KindOption> KindOptions { get; }

    public IReadOnlyList<MassUnitOption> MassUnitOptions { get; }

    public ObservableCollection<FieldMappingViewModel> Mappings { get; } = [];

    public ObservableCollection<PreviewRow> Preview { get; } = [];

    public bool HasFile => _table is not null;

    /// <summary>Weights matter only for the kinds that have them.</summary>
    public bool HasMasses => SelectedKind.Kind is ImportKind.Vehicles or ImportKind.Weighings;

    public string KindHelp => SelectedKind.Kind switch
    {
        ImportKind.Vehicles => "Справочник транспорта: госномер, прицеп, водитель, контрагент, груз, тара и допустимая масса.",
        ImportKind.Companies => "Справочник контрагентов: название, ИНН, заметка.",
        ImportKind.Cargos => "Справочник грузов: название.",
        ImportKind.Drivers => "Справочник водителей: ФИО и телефон.",
        _ => "История взвешиваний из прежней системы. Записи получат отметку об импорте в истории; дубли пропускаются, поэтому один файл можно загружать повторно.",
    };

    [RelayCommand(CanExecute = nameof(CanChooseFile))]
    private async Task ChooseFileAsync()
    {
        ErrorMessage = InfoMessage = "";
        var path = _dialogs.PickOpenFile(
            "Файл для импорта", "Таблицы Excel и CSV|*.xlsx;*.xls;*.xlsm;*.csv;*.txt;*.tsv|Все файлы|*.*");
        if (path is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _table = await _reader.ReadAsync(path);
            FileName = $"{_table.SourceName} · строк с данными: {_table.Rows.Count:N0}";
            OnPropertyChanged(nameof(HasFile));
            BuildMappings();
            await ReplanAsync();
        }
        catch (ValidationException ex)
        {
            ResetFile();
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ResetFile();
            ErrorMessage = $"Не удалось прочитать файл: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChooseFile() => !IsBusy && _user.Can(Permission.ImportData);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (_plan is not { HasWork: true } plan || _user.User is not { } actor || !_user.Can(Permission.ImportData))
        {
            return;
        }

        var question = $"Добавить записей: {plan.ToInsert:N0}, обновить: {plan.ToUpdate:N0}.\n" +
                       $"Раздел: {ImportSchema.Title(plan.Kind)}. Файл: {plan.SourceName}.\n\n" +
                       "Отменить импорт после записи нельзя (только восстановить базу из резервной копии). Продолжить?";
        if (!_dialogs.Confirm("Импорт данных", question))
        {
            return;
        }

        ErrorMessage = InfoMessage = "";
        IsBusy = true;
        Progress = 0;
        ProgressMax = Math.Max(1, plan.ToInsert + plan.ToUpdate);
        try
        {
            var outcome = await _import.ApplyAsync(plan, actor, new Progress<int>(n => Progress = n));
            InfoMessage = $"Готово: добавлено {outcome.Inserted:N0}, обновлено {outcome.Updated:N0}, пропущено {outcome.Skipped:N0}, с ошибками {outcome.Errors:N0}.";
            await ReplanAsync(); // the file now reads as "already there": a second press would add nothing
        }
        catch (Exception ex) when (ex is ValidationException or DuplicateException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Импорт не выполнен, данные не изменены: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImport() => !IsBusy && HasPlan && _plan is { HasWork: true } && _user.Can(Permission.ImportData);

    [RelayCommand]
    private void SaveTemplate()
    {
        ErrorMessage = InfoMessage = "";
        var path = _dialogs.PickSaveFile("Шаблон для импорта", ImportTemplates.FileName(SelectedKind.Kind), "CSV (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, ImportTemplates.Csv(SelectedKind.Kind), new UTF8Encoding(true));
            InfoMessage = "Шаблон сохранён. Заполните его в Excel и загрузите обратно.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить файл: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveProblems))]
    private void SaveProblems()
    {
        ErrorMessage = InfoMessage = "";
        if (_plan is null || _table is null)
        {
            return;
        }

        var path = _dialogs.PickSaveFile(
            "Строки с проблемами", $"проблемы-{_time.GetLocalNow():yyyy-MM-dd-HHmm}.csv", "CSV (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            var problems = _plan.Rows.Where(r => r.Severity != RowSeverity.Ok || r.Action == ImportAction.Skip).ToList();
            var headers = new[] { "Строка", "Что будет", "Замечание" }.Concat(_table.Headers).ToList();
            var rows = problems.Select(r => (IReadOnlyList<string>)new[] { r.RowNumber.ToString(), ActionText(r), r.Message }
                .Concat(_table.Rows[r.RowNumber - 2]).ToList());
            File.WriteAllText(path, Csv.Build(headers, rows), new UTF8Encoding(true));
            InfoMessage = $"Сохранено строк: {problems.Count}. Исправьте их в Excel и загрузите файл снова.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить файл: {ex.Message}";
        }
    }

    private bool CanSaveProblems() => !IsBusy && _plan is not null && _plan.Rows.Any(r => r.Severity != RowSeverity.Ok || r.Action == ImportAction.Skip);

    partial void OnSelectedKindChanged(KindOption value)
    {
        OnPropertyChanged(nameof(HasMasses));
        if (_table is not null)
        {
            BuildMappings();
            _ = ReplanAsync();
        }
    }

    partial void OnSelectedMassUnitChanged(MassUnitOption value) => _ = ReplanAsync();

    partial void OnUpdateExistingChanged(bool value) => _ = ReplanAsync();

    partial void OnShowProblemsOnlyChanged(bool value) => RefreshPreview();

    private void BuildMappings()
    {
        if (_table is null)
        {
            return;
        }

        _rebuilding = true;
        try
        {
            var kind = SelectedKind.Kind;
            var auto = ColumnMapping.Auto(kind, _table.Headers);
            var columns = new List<ColumnOption> { new(-1, "— не импортировать —") };
            columns.AddRange(_table.Headers.Select((h, i) => new ColumnOption(i, h)));

            Mappings.Clear();
            foreach (var field in ImportSchema.Fields(kind))
            {
                var picked = columns.First(c => c.Index == auto[field.Key]);
                Mappings.Add(new FieldMappingViewModel(field, columns, picked, () => { if (!_rebuilding) _ = ReplanAsync(); }));
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private async Task ReplanAsync()
    {
        if (_table is null)
        {
            return;
        }

        var mine = ++_version;
        var table = _table;
        var kind = SelectedKind.Kind;
        var mapping = new ColumnMapping();
        foreach (var m in Mappings)
        {
            mapping[m.Key] = m.Selected.Index;
        }

        var options = new ImportOptions { UpdateExisting = UpdateExisting, MassUnit = SelectedMassUnit.Unit };
        try
        {
            var plan = await _import.PlanAsync(kind, table, mapping, options);
            if (mine != _version)
            {
                return; // the user changed something meanwhile; a newer plan is on its way
            }

            _plan = plan;
            HasPlan = true;
            ErrorMessage = "";
            Summary = $"К добавлению: {plan.ToInsert:N0} · к обновлению: {plan.ToUpdate:N0} · без действий: {plan.Skipped:N0} · " +
                      $"с ошибками: {plan.Errors:N0}" + (plan.Warnings > 0 ? $" · с замечаниями: {plan.Warnings:N0}" : "");
            RefreshPreview();
        }
        catch (ValidationException ex)
        {
            if (mine == _version)
            {
                _plan = null;
                HasPlan = false;
                Preview.Clear();
                PreviewNote = "";
                Summary = "";
                ErrorMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (mine == _version)
            {
                _plan = null;
                HasPlan = false;
                ErrorMessage = $"Не удалось разобрать файл: {ex.Message}";
            }
        }

        ImportCommand.NotifyCanExecuteChanged();
        SaveProblemsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPreview()
    {
        Preview.Clear();
        if (_plan is null)
        {
            PreviewNote = "";
            return;
        }

        var rows = _plan.Rows.Where(r => !ShowProblemsOnly || r.Severity != RowSeverity.Ok || r.Action == ImportAction.Skip).ToList();
        foreach (var r in rows.Take(MaxPreview))
        {
            Preview.Add(new PreviewRow(
                r.RowNumber, ActionText(r), r.Message, r.Summary, r.Severity == RowSeverity.Error, r.Severity == RowSeverity.Warning));
        }

        PreviewNote = rows.Count > MaxPreview ? $"Показаны первые {MaxPreview} из {rows.Count:N0} строк." : "";
    }

    private static string ActionText(PlannedRow r) => r.Severity == RowSeverity.Error
        ? "Ошибка"
        : r.Action switch { ImportAction.Insert => "Добавить", ImportAction.Update => "Обновить", _ => "Пропуск" };

    private void ResetFile()
    {
        _table = null;
        _plan = null;
        _version++;
        HasPlan = false;
        FileName = Summary = PreviewNote = "";
        Mappings.Clear();
        Preview.Clear();
        OnPropertyChanged(nameof(HasFile));
    }
}
