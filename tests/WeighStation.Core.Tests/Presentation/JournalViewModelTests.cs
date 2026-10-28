using System.Text;
using WeighStation.Core.Models;
using WeighStation.Presentation.Pages;

namespace WeighStation.Core.Tests.Presentation;

public class JournalViewModelTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly JournalViewModel _vm;

    public JournalViewModelTests() => _vm = _app.NewJournal();

    public void Dispose() => _app.Dispose();

    private static DateTimeOffset Day(int day, int hour = 12) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_to_today()
    {
        Assert.Equal(new DateTime(2026, 10, 28), _vm.DateFrom);
        Assert.Equal(new DateTime(2026, 10, 28), _vm.DateTo);
    }

    [Fact]
    public async Task Search_shows_todays_weighings_and_a_summary_without_cancelled_ones()
    {
        await _app.AddWeighing("А111АА77", net: 20000, time: Day(28, 9));
        await _app.AddWeighing("В222ВВ77", net: 10000, time: Day(28, 10));
        await _app.AddWeighing("С333СС77", net: 5000, status: WeighingStatus.Cancelled, time: Day(28, 11));
        await _app.AddWeighing("Е444ЕЕ77", net: 99999, time: Day(27));

        await _vm.ActivateAsync();

        Assert.Equal(3, _vm.Rows.Count);
        Assert.Equal("С333СС 77", _vm.Rows[0].Plate);
        Assert.Equal("Показано 3 · нетто 30 000 кг", _vm.Summary);
    }

    [Fact]
    public async Task Filters_combine()
    {
        await _app.AddWeighing("А111АА77", company: "Север", time: Day(28, 9));
        await _app.AddWeighing("А222АА77", company: "Юг", time: Day(28, 10));
        await _app.AddWeighing("В333ВВ77", company: "Север", status: WeighingStatus.ManualEdit, time: Day(28, 11));

        _vm.PlateFilter = "а";
        _vm.CompanyFilter = "СЕВЕР";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("А111АА 77", Assert.Single(_vm.Rows).Plate);

        _vm.PlateFilter = "";
        _vm.StatusFilter = _vm.StatusOptions.Single(o => o.Value == WeighingStatus.ManualEdit);
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("В333ВВ 77", Assert.Single(_vm.Rows).Plate);
    }

    [Fact]
    public async Task Period_filter_includes_whole_last_day_and_nothing_after()
    {
        await _app.AddWeighing("А111АА77", time: Day(26, 23));
        await _app.AddWeighing("А222АА77", time: Day(27, 23));
        await _app.AddWeighing("А333АА77", time: Day(28, 0));

        _vm.DateFrom = new DateTime(2026, 10, 27);
        _vm.DateTo = new DateTime(2026, 10, 27);
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("А222АА 77", Assert.Single(_vm.Rows).Plate);
    }

    [Fact]
    public async Task Reset_filters_restores_today_and_all_statuses()
    {
        await _app.AddWeighing("А111АА77", time: Day(28));
        _vm.PlateFilter = "ZZZ";
        _vm.StatusFilter = _vm.StatusOptions[3];

        await _vm.ResetFiltersCommand.ExecuteAsync(null);

        Assert.Equal("", _vm.PlateFilter);
        Assert.Null(_vm.StatusFilter.Value);
        Assert.Single(_vm.Rows);
    }

    [Fact]
    public async Task Selecting_a_row_shows_details_and_history()
    {
        var w = await _app.AddWeighing("А123ВС197", company: "Север", net: 28460);
        await _vm.ActivateAsync();

        _vm.SelectedRow = _vm.Rows[0];
        await TestApp.Eventually(() => _vm.History.Count > 0, "history load");

        Assert.Equal($"Взвешивание № {w.Id}", _vm.DetailTitle);
        Assert.Equal("28 460", _vm.DetailNet);
        Assert.Equal("А123ВС 197", _vm.EditPlate);
        Assert.Equal("Север", _vm.EditCompany);
        Assert.True(_vm.CanEdit);
        Assert.False(_vm.IsDirty);
        Assert.Equal("Вес зафиксирован оператором", _vm.History[0].Text);
        Assert.True(_vm.PrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Clearing_the_selection_empties_the_detail_pane()
    {
        await _app.AddWeighing();
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows[0];

        _vm.SelectedRow = null;

        Assert.Equal("", _vm.DetailTitle);
        Assert.Empty(_vm.History);
        Assert.False(_vm.CanEdit);
        Assert.False(_vm.PrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task Editing_marks_dirty_saving_writes_history_and_manual_edit_status()
    {
        var w = await _app.AddWeighing("А123ВС197", company: "Старый");
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows[0];
        Assert.False(_vm.SaveEditCommand.CanExecute(null));

        _vm.EditCompany = "Новый";
        _vm.EditDriver = "Петров";
        Assert.True(_vm.IsDirty);
        Assert.True(_vm.SaveEditCommand.CanExecute(null));

        await _vm.SaveEditCommand.ExecuteAsync(null);

        var after = (await _app.RealRepo.GetAsync(w.Id))!;
        Assert.Equal("Новый", after.Counterparty);
        Assert.Equal("Петров", after.Driver);
        Assert.Equal(WeighingStatus.ManualEdit, after.Status);
        var history = await _app.RealRepo.GetHistoryAsync(w.Id);
        Assert.Equal("Правка: Водитель: «» → «Петров»; Контрагент: «Старый» → «Новый»", history[^1].Text);
        Assert.Equal("Тестов А.А.", history[^1].User);
        Assert.Equal("Изменения сохранены.", _vm.InfoMessage);
        Assert.False(_vm.IsDirty);
        Assert.Equal("Ручная правка", _vm.Rows[0].Status);
    }

    [Fact]
    public async Task Reverting_an_edit_clears_the_dirty_flag()
    {
        await _app.AddWeighing(company: "Север");
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows[0];

        _vm.EditCompany = "Другой";
        Assert.True(_vm.IsDirty);
        _vm.EditCompany = "Север";
        Assert.False(_vm.IsDirty);
    }

    [Fact]
    public async Task Plate_edit_is_normalised_and_cannot_be_blank()
    {
        var w = await _app.AddWeighing("А123ВС197");
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows[0];

        _vm.EditPlate = "";
        await _vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Equal("Госномер не может быть пустым.", _vm.ErrorMessage);

        _vm.EditPlate = "k 507 mp 50";
        await _vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Equal("К507МР50", (await _app.RealRepo.GetAsync(w.Id))!.Plate);
    }

    [Fact]
    public async Task Cancelling_asks_for_confirmation_and_then_locks_the_record()
    {
        var w = await _app.AddWeighing("А123ВС197");
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows[0];

        _app.Dialogs.ConfirmAnswer = false;
        await _vm.CancelWeighingCommand.ExecuteAsync(null);
        Assert.Equal(WeighingStatus.Completed, (await _app.RealRepo.GetAsync(w.Id))!.Status);
        Assert.Contains("А123ВС 197", _app.Dialogs.Questions.Single());

        _app.Dialogs.ConfirmAnswer = true;
        await _vm.CancelWeighingCommand.ExecuteAsync(null);

        Assert.Equal(WeighingStatus.Cancelled, (await _app.RealRepo.GetAsync(w.Id))!.Status);
        Assert.Equal("Отменено", _vm.Rows[0].Status);
        await TestApp.Eventually(() => !_vm.CanEdit, "detail reload");
        Assert.Equal("Запись отменена оператором", (await _app.RealRepo.GetHistoryAsync(w.Id))[^1].Text);
    }

    [Fact]
    public async Task Export_writes_utf8_csv_with_bom_of_the_displayed_rows()
    {
        await _app.AddWeighing("А123ВС197", company: "ООО «Север»");
        await _vm.ActivateAsync();
        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "out.csv");

        _vm.ExportCommand.Execute(null);

        var bytes = File.ReadAllBytes(_app.Dialogs.SavePath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("№;Дата;Время;Номер", text);
        Assert.Contains("А123ВС 197", text);
        Assert.Contains("ООО «Север»", text);
        Assert.Equal("journal-2026-10-28.csv", _app.Dialogs.SaveSuggestions.Single());
        Assert.Equal("Сохранено записей: 1.", _vm.InfoMessage);
    }

    [Fact]
    public async Task Export_does_nothing_when_cancelled_or_empty()
    {
        _vm.ExportCommand.Execute(null);
        Assert.Equal("Нечего экспортировать: в списке нет записей.", _vm.InfoMessage);
        Assert.Empty(_app.Dialogs.SaveSuggestions);

        await _app.AddWeighing();
        await _vm.ActivateAsync();
        _app.Dialogs.SavePath = null;
        _vm.ExportCommand.Execute(null);
        Assert.Equal("", _vm.ErrorMessage);
    }

    [Fact]
    public async Task Export_to_an_unwritable_path_reports_the_error()
    {
        await _app.AddWeighing();
        await _vm.ActivateAsync();
        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "no-such-dir", "x.csv");

        _vm.ExportCommand.Execute(null);

        Assert.StartsWith("Не удалось сохранить файл:", _vm.ErrorMessage);
    }

    [Fact]
    public async Task Print_sends_the_selected_weighing_with_its_history_to_the_printer()
    {
        var w = await _app.AddWeighing();
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows[0];
        await TestApp.Eventually(() => _vm.History.Count > 0, "history load");

        _vm.PrintCommand.Execute(null);

        var (printed, history) = Assert.Single(_app.Printer.Printed);
        Assert.Equal(w.Id, printed.Id);
        Assert.Single(history);
    }

    [Fact]
    public async Task Printer_failure_is_reported()
    {
        await _app.AddWeighing();
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows[0];
        _app.Printer.FailWith = new InvalidOperationException("нет принтера");

        _vm.PrintCommand.Execute(null);

        Assert.Equal("Не удалось напечатать талон: нет принтера", _vm.ErrorMessage);
    }

    [Fact]
    public async Task Selection_survives_a_new_search()
    {
        var a = await _app.AddWeighing("А111АА77", time: Day(28, 9));
        await _app.AddWeighing("В222ВВ77", time: Day(28, 10));
        await _vm.ActivateAsync();
        _vm.SelectedRow = _vm.Rows.Single(r => r.Id == a.Id);

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(a.Id, _vm.SelectedRow?.Id);
    }
}
