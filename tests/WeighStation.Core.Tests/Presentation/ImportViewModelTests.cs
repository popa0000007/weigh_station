using System.Text;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Import;
using WeighStation.Core.Models;
using WeighStation.Presentation.Pages;

namespace WeighStation.Core.Tests.Presentation;

public class ImportViewModelTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<ImportViewModel> OpenAsync(string csv, ImportKind kind = ImportKind.Vehicles, string name = "data.csv")
    {
        var vm = _app.NewImport();
        vm.SelectedKind = vm.KindOptions.Single(k => k.Kind == kind);
        _app.Dialogs.OpenPath = _app.WriteCsv(name, csv);
        await vm.ChooseFileCommand.ExecuteAsync(null);
        return vm;
    }

    private static ColumnOption Pick(FieldMappingViewModel m, string column) => m.Options.Single(o => o.Name == column);

    [Fact]
    public async Task Choosing_a_file_maps_columns_and_shows_the_summary_and_preview()
    {
        var vm = await OpenAsync("Госномер;Контрагент;Тара\nА111АА77;Север;14000\nВ222ВВ77;Юг;13000\n;Нет номера;1\n");

        Assert.True(vm.HasFile);
        Assert.Equal("data.csv · строк с данными: 3", vm.FileName);
        Assert.Equal("Госномер", vm.Mappings.Single(m => m.Key == "plate").Selected.Name);
        Assert.Equal("— не импортировать —", vm.Mappings.Single(m => m.Key == "trailer").Selected.Name);
        Assert.Equal("К добавлению: 2 · к обновлению: 0 · без действий: 0 · с ошибками: 1", vm.Summary);
        Assert.Equal(3, vm.Preview.Count);
        Assert.Equal(["Добавить", "Добавить", "Ошибка"], vm.Preview.Select(p => p.Action));
        Assert.True(vm.Preview[2].IsError);
        Assert.Equal("", vm.ErrorMessage);
        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.Equal("*", vm.Mappings[0].Title[^1..]); // required fields are marked
    }

    [Fact]
    public async Task Cancelling_the_file_dialog_changes_nothing()
    {
        var vm = _app.NewImport();
        _app.Dialogs.OpenPath = null;
        await vm.ChooseFileCommand.ExecuteAsync(null);
        Assert.False(vm.HasFile);
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.Contains("xlsx", _app.Dialogs.OpenFilters.Single());
    }

    [Fact]
    public async Task A_missing_required_column_explains_itself_and_picking_a_column_fixes_it()
    {
        var vm = await OpenAsync("Колонка А;Груз\nА111АА77;Щебень\n");

        Assert.Contains("«Госномер»", vm.ErrorMessage);
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.Empty(vm.Preview);

        var plate = vm.Mappings.Single(m => m.Key == "plate");
        plate.Selected = Pick(plate, "Колонка А");
        await TestApp.Eventually(() => vm.HasPlan, "re-plan after mapping");

        Assert.Equal("", vm.ErrorMessage);
        Assert.Equal("К добавлению: 1 · к обновлению: 0 · без действий: 0 · с ошибками: 0", vm.Summary);
        Assert.True(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Unmapping_a_field_removes_it_from_the_import()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77" });
        var vm = await OpenAsync("Госномер;Водитель\nА111АА77;Петров\n");
        vm.UpdateExisting = true;
        await TestApp.Eventually(() => vm.Summary.Contains("к обновлению: 1"), "update plan");

        var driver = vm.Mappings.Single(m => m.Key == "driver");
        driver.Selected = driver.Options[0];
        await TestApp.Eventually(() => vm.Summary.Contains("к обновлению: 0"), "re-plan without the driver");

        Assert.Equal("Без изменений.", vm.Preview[0].Message);
    }

    [Fact]
    public async Task Changing_the_kind_remaps_the_same_file()
    {
        var vm = await OpenAsync("Название;ИНН\nООО «Север»;7700000000\n", ImportKind.Companies);
        Assert.Equal("К добавлению: 1 · к обновлению: 0 · без действий: 0 · с ошибками: 0", vm.Summary);

        vm.SelectedKind = vm.KindOptions.Single(k => k.Kind == ImportKind.Cargos);
        await TestApp.Eventually(() => vm.Mappings.Count == 1, "cargo mapping");

        Assert.Equal("Название", vm.Mappings[0].Selected.Name);
        Assert.False(vm.HasMasses);
        Assert.Contains("грузов", vm.KindHelp);
    }

    [Fact]
    public async Task The_mass_unit_changes_how_weights_are_read()
    {
        var vm = await OpenAsync("Госномер;Тара\nА111АА77;14,32\n");
        Assert.True(vm.HasMasses);
        // Read as kilograms, "14,32" is 14 kg: importable, but the program suspects the file is in tonnes.
        Assert.True(vm.Preview[0].IsWarning);
        Assert.Contains("тонны", vm.Preview[0].Message);
        Assert.Contains("с замечаниями: 1", vm.Summary);

        vm.SelectedMassUnit = vm.MassUnitOptions.Single(u => u.Unit == MassUnit.Tonnes);
        await TestApp.Eventually(() => !vm.Preview[0].IsWarning, "re-plan in tonnes");
        Assert.Equal("К добавлению: 1 · к обновлению: 0 · без действий: 0 · с ошибками: 0", vm.Summary);
    }

    [Fact]
    public async Task Import_asks_first_then_writes_and_a_second_press_has_nothing_to_do()
    {
        var vm = await OpenAsync("Госномер;Контрагент\nА111АА77;Север\nВ222ВВ77;Юг\n");

        _app.Dialogs.ConfirmAnswer = false;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Empty(await _app.Vehicles.GetAllAsync());
        Assert.Contains("Отменить импорт после записи нельзя", _app.Dialogs.Questions.Last());

        _app.Dialogs.ConfirmAnswer = true;
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal("Готово: добавлено 2, обновлено 0, пропущено 0, с ошибками 0.", vm.InfoMessage);
        Assert.Equal(["А111АА77", "В222ВВ77"], (await _app.Vehicles.GetAllAsync()).Select(v => v.Plate));
        Assert.Equal("К добавлению: 0 · к обновлению: 0 · без действий: 2 · с ошибками: 0", vm.Summary);
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.Equal("import", (await _app.Audit.GetRecentAsync(1)).Single().Action);
    }

    [Fact]
    public async Task Weighing_history_import_end_to_end()
    {
        var vm = await OpenAsync(
            "Дата;Номер;Груз;Брутто;Тара\n27.10.2026 09:00;А111АА77;Щебень;42 780;14 320\n28.10.2026 10:00;В222ВВ77;Песок;38 000;\nплохая;С333СС77;;1;\n",
            ImportKind.Weighings);
        Assert.Equal("К добавлению: 2 · к обновлению: 0 · без действий: 0 · с ошибками: 1", vm.Summary);

        _app.Dialogs.ConfirmAnswer = true;
        await vm.ImportCommand.ExecuteAsync(null);

        var stored = (await _app.RealRepo.QueryAsync(new WeighingQuery())).OrderBy(w => w.Time).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(28460, stored[0].NetKg);
        Assert.Null(stored[1].NetKg);
        Assert.Contains("Импортировано", (await _app.RealRepo.GetHistoryAsync(stored[0].Id)).Single().Text);
    }

    [Fact]
    public async Task Only_administrators_can_use_the_import()
    {
        var vm = await OpenAsync("Госномер\nА111АА77\n");
        Assert.True(vm.ImportCommand.CanExecute(null));

        _app.SignInAs(UserRole.Manager);

        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.False(vm.ChooseFileCommand.CanExecute(null));
        _app.Dialogs.ConfirmAnswer = true;
        await vm.ImportCommand.ExecuteAsync(null); // a direct call is refused as well
        Assert.Empty(await _app.Vehicles.GetAllAsync());
    }

    [Fact]
    public async Task Unreadable_files_are_reported_and_leave_the_screen_clean()
    {
        var vm = _app.NewImport();
        _app.Dialogs.OpenPath = _app.WriteCsv("x.pdf", "not a table");
        await vm.ChooseFileCommand.ExecuteAsync(null);

        Assert.Contains("не поддерживается", vm.ErrorMessage);
        Assert.False(vm.HasFile);
        Assert.Empty(vm.Mappings);

        _app.Dialogs.OpenPath = _app.WriteCsv("empty.csv", "");
        await vm.ChooseFileCommand.ExecuteAsync(null);
        Assert.Contains("пуст", vm.ErrorMessage);
    }

    [Fact]
    public async Task A_new_file_replaces_the_old_one_and_clears_old_messages()
    {
        var vm = await OpenAsync("Госномер\nА111АА77\n");
        _app.Dialogs.OpenPath = _app.WriteCsv("second.csv", "Госномер\nВ222ВВ77\nЕ333ЕЕ77\n");
        await vm.ChooseFileCommand.ExecuteAsync(null);

        Assert.StartsWith("second.csv", vm.FileName);
        Assert.Equal(2, vm.Preview.Count);
    }

    [Fact]
    public async Task Preview_is_capped_and_can_show_only_the_problems()
    {
        var sb = new StringBuilder("Название\n");
        for (var i = 0; i < 700; i++)
        {
            sb.Append("Груз ").Append(i).Append('\n');
        }

        await _app.Cargos.SaveAsync(new CargoType { Name = "Груз 5" });
        var vm = await OpenAsync(sb.ToString(), ImportKind.Cargos);

        Assert.Equal(500, vm.Preview.Count);
        Assert.Equal("Показаны первые 500 из 700 строк.", vm.PreviewNote);

        vm.ShowProblemsOnly = true;

        var only = Assert.Single(vm.Preview);
        Assert.Equal("Пропуск", only.Action);
        Assert.Equal("", vm.PreviewNote);
    }

    [Fact]
    public async Task The_template_it_writes_is_a_valid_import_file()
    {
        var vm = _app.NewImport();
        vm.SelectedKind = vm.KindOptions.Single(k => k.Kind == ImportKind.Vehicles);
        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "template.csv");

        vm.SaveTemplateCommand.Execute(null);

        var bytes = File.ReadAllBytes(_app.Dialogs.SavePath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("Госномер", Encoding.UTF8.GetString(bytes));
        Assert.Equal("шаблон-vehicles.csv", _app.Dialogs.SaveSuggestions.Single());

        _app.Dialogs.OpenPath = _app.Dialogs.SavePath;
        await vm.ChooseFileCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ErrorMessage);
        Assert.Equal(1, vm.Preview.Count(p => p.Action == "Добавить"));
    }

    [Fact]
    public async Task Problem_rows_can_be_saved_with_their_original_cells_for_fixing()
    {
        var vm = await OpenAsync("Госномер;Тара\nА111АА77;14000\n;5\nВ222ВВ77;много\n");
        Assert.True(vm.SaveProblemsCommand.CanExecute(null));
        _app.Dialogs.SavePath = Path.Combine(_app.Dir, "problems.csv");

        vm.SaveProblemsCommand.Execute(null);

        var lines = File.ReadAllLines(_app.Dialogs.SavePath);
        Assert.Equal("Строка;Что будет;Замечание;Госномер;Тара", lines[0].TrimStart('﻿'));
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("3;Ошибка;Не указан госномер.", lines[1]);
        Assert.StartsWith("4;Ошибка;Тара «много» — не число.", lines[2]);
        Assert.Equal("Сохранено строк: 2. Исправьте их в Excel и загрузите файл снова.", vm.InfoMessage);
    }

    [Fact]
    public async Task Nothing_to_save_when_the_file_has_no_problems()
    {
        var vm = await OpenAsync("Госномер\nА111АА77\n");
        Assert.False(vm.SaveProblemsCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failing_import_leaves_the_data_unchanged_and_says_so()
    {
        var vm = await OpenAsync("Госномер\nА111АА77\nВ222ВВ77\n");
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "В222ВВ77" }); // appears after the preview was built
        _app.Dialogs.ConfirmAnswer = true;

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Contains("Импорт отменён", vm.ErrorMessage);
        Assert.Equal(["В222ВВ77"], (await _app.Vehicles.GetAllAsync()).Select(v => v.Plate));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Update_existing_checkbox_switches_between_skip_and_update()
    {
        await _app.Companies.SaveAsync(new Company { Name = "Север" });
        var vm = await OpenAsync("Название;ИНН\nСевер;7700000000\n", ImportKind.Companies);
        Assert.Equal("Пропуск", vm.Preview[0].Action);

        vm.UpdateExisting = true;
        await TestApp.Eventually(() => vm.Preview[0].Action == "Обновить", "update plan");

        _app.Dialogs.ConfirmAnswer = true;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal("7700000000", (await _app.Companies.GetAllAsync()).Single().Inn);
    }
}
