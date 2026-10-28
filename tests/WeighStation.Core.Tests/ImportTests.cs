using System.Text;
using ClosedXML.Excel;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Import;
using WeighStation.Core.Models;
using WeighStation.Core.Security;
using WeighStation.Core.Tests.Presentation;
using WeighStation.Infrastructure.Import;
using WeighStation.Infrastructure.Persistence;

namespace WeighStation.Core.Tests;

public class CsvParserTests
{
    [Fact]
    public void Parses_quoted_cells_embedded_separators_quotes_and_line_breaks()
    {
        var rows = CsvParser.Parse("a;b;c\r\n\"x;y\";\"say \"\"hi\"\"\";\"line1\nline2\"\r\n1;2;3");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["x;y", "say \"hi\"", "line1\nline2"], rows[1]);
        Assert.Equal(["1", "2", "3"], rows[2]);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a,b,c\n1,2,3", ',')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("a|b|c", '|')]
    [InlineData("название;цена, руб;вес\n", ';')] // a comma inside a header must not win over the real separator
    [InlineData("\"a;b\",c,d\n1,2,3", ',')]
    public void Detects_the_delimiter(string text, char expected) => Assert.Equal(expected, CsvParser.DetectDelimiter(text));

    [Fact]
    public void Handles_bom_empty_cells_trailing_newline_and_both_line_endings()
    {
        var rows = CsvParser.Parse("﻿a;b\n;2\r\n3;\n");
        Assert.Equal([["a", "b"], ["", "2"], ["3", ""]], rows);
    }

    [Fact]
    public void A_last_line_without_a_newline_is_kept() => Assert.Equal(2, CsvParser.Parse("a;b\n1;2").Count);

    [Fact]
    public void ToTable_trims_pads_names_blank_headers_and_drops_empty_rows()
    {
        var table = CsvParser.ToTable(CsvParser.Parse(" Номер ; ;Груз\n А123 ;x\n;;\n\nБ456;y;z;extra").Select(r => (IReadOnlyList<string>)r).ToList(), "f.csv");

        Assert.Equal(["Номер", "Колонка 2", "Груз", "Колонка 4"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["А123", "x", "", ""], table.Rows[0]);
        Assert.Equal(["Б456", "y", "z", "extra"], table.Rows[1]);
        Assert.Equal("f.csv", table.SourceName);
    }

    [Fact]
    public void An_empty_file_is_reported() =>
        Assert.Contains("пуст", Assert.Throws<ValidationException>(() => CsvParser.ToTable([], "f.csv")).Message);
}

public class ImportParsingTests
{
    [Theory]
    [InlineData("42780", MassUnit.Kilograms, 42780)]
    [InlineData("42 780", MassUnit.Kilograms, 42780)]
    [InlineData("42 780", MassUnit.Kilograms, 42780)]
    [InlineData("42 780 кг", MassUnit.Kilograms, 42780)]
    [InlineData("42780,4", MassUnit.Kilograms, 42780)]
    [InlineData("42780.6", MassUnit.Kilograms, 42781)]
    [InlineData("42.780", MassUnit.Kilograms, 42780)] // dot as thousands separator
    [InlineData("42,78", MassUnit.Tonnes, 42780)]
    [InlineData("42.78", MassUnit.Tonnes, 42780)]
    [InlineData("42,78 т", MassUnit.Tonnes, 42780)]
    [InlineData("14", MassUnit.Tonnes, 14000)]
    [InlineData("0,5", MassUnit.Tonnes, 500)]
    public void Parses_masses_in_the_ways_people_write_them(string text, MassUnit unit, int expectedKg)
    {
        Assert.True(ImportParsing.TryParseMass(text, unit, out var kg), text);
        Assert.Equal(expectedKg, kg);
    }

    [Fact]
    public void Masses_above_a_million_kilograms_are_refused() =>
        Assert.False(ImportParsing.TryParseMass("1.042.780", MassUnit.Kilograms, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("много")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12abc")]
    [InlineData("5000000")]
    [InlineData("1..2")]
    public void Rejects_what_is_not_a_plausible_mass(string text) =>
        Assert.False(ImportParsing.TryParseMass(text, MassUnit.Kilograms, out _));

    [Theory]
    [InlineData("28.10.2026 14:32", 2026, 10, 28, 14, 32)]
    [InlineData("28.10.2026 14:32:45", 2026, 10, 28, 14, 32)]
    [InlineData("28.10.2026", 2026, 10, 28, 0, 0)]
    [InlineData("8.1.2026 9:05", 2026, 1, 8, 9, 5)]
    [InlineData("2026-10-28 14:32:00", 2026, 10, 28, 14, 32)]
    [InlineData("2026-10-28T14:32:00", 2026, 10, 28, 14, 32)]
    [InlineData("28/10/2026 14:32", 2026, 10, 28, 14, 32)]
    [InlineData("28.10.26 14:32", 2026, 10, 28, 14, 32)]
    [InlineData("46323.6", 2026, 10, 28, 14, 24)] // Excel serial number
    public void Parses_dates_in_common_formats(string text, int y, int mo, int d, int h, int mi)
    {
        Assert.True(ImportParsing.TryParseDateTime(text, null, out var dt), text);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0));
    }

    [Theory]
    [InlineData("14:32", 14, 32)]
    [InlineData("9:05:30", 9, 5)]
    [InlineData("0.5", 12, 0)] // Excel stores times as a fraction of the day
    public void Combines_date_and_time_columns(string time, int h, int m)
    {
        Assert.True(ImportParsing.TryParseDateTime("28.10.2026", time, out var dt));
        Assert.Equal(new DateTime(2026, 10, 28, h, m, 0), new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("вчера")]
    [InlineData("31.02.2026")]
    [InlineData("2026-13-01")]
    [InlineData("12")]
    public void Rejects_unknown_dates(string text) => Assert.False(ImportParsing.TryParseDateTime(text, null, out _));

    [Fact]
    public void A_bad_time_makes_the_whole_date_invalid() =>
        Assert.False(ImportParsing.TryParseDateTime("28.10.2026", "позже", out _));

    [Theory]
    [InlineData("Брутто", false)]
    [InlineData("загруженный", false)]
    [InlineData("gross", false)]
    [InlineData("Тара", true)]
    [InlineData("порожний", true)]
    [InlineData("TARE", true)]
    [InlineData("", null)]
    [InlineData("???", null)]
    public void Recognises_the_operation(string text, bool? isTare) => Assert.Equal(isTare, ImportParsing.ParseKindIsTare(text));
}

public class ColumnMappingTests
{
    [Fact]
    public void Maps_russian_headers_with_punctuation_and_units()
    {
        var m = ColumnMapping.Auto(ImportKind.Weighings,
            ["Дата взвешивания", "Гос. номер ТС", "Контрагент", "Груз", "Брутто, кг", "Тара, кг", "Нетто, кг", "Водитель", "Прицеп"]);

        Assert.Equal(0, m["date"]);
        Assert.Equal(1, m["plate"]);
        Assert.Equal(2, m["company"]);
        Assert.Equal(3, m["cargo"]);
        Assert.Equal(4, m["gross"]);
        Assert.Equal(5, m["tare"]);
        Assert.Equal(6, m["net"]);
        Assert.Equal(7, m["driver"]);
        Assert.Equal(8, m["trailer"]);
        Assert.False(m.IsMapped("kind"));
    }

    [Fact]
    public void Exact_match_beats_a_fuzzy_one()
    {
        // "Допустимая масса тары" contains "тара"-like text, but the plain "Тара" column must feed the tare field.
        var m = ColumnMapping.Auto(ImportKind.Vehicles, ["Госномер", "Допустимая масса", "Тара"]);
        Assert.Equal(2, m["tare"]);
        Assert.Equal(1, m["max"]);
    }

    [Fact]
    public void One_column_feeds_one_field_only()
    {
        var m = ColumnMapping.Auto(ImportKind.Vehicles, ["Номер"]);
        Assert.Equal(0, m["plate"]);
        Assert.False(m.IsMapped("trailer"));
    }

    [Fact]
    public void Latin_and_mixed_case_headers_are_understood()
    {
        var m = ColumnMapping.Auto(ImportKind.Companies, ["NAME", "ИНН"]);
        Assert.Equal(0, m["name"]);
        Assert.Equal(1, m["inn"]);
    }

    [Fact]
    public void Unknown_headers_stay_unmapped_and_yo_equals_ye()
    {
        var m = ColumnMapping.Auto(ImportKind.Cargos, ["Что-то", "Наименование"]);
        Assert.Equal(1, m["name"]);
        Assert.Equal("перевозчик", ColumnMapping.Normalize("Перевозчик"));
        Assert.Equal("елка", ColumnMapping.Normalize("Ёлка"));
    }

    [Fact]
    public void Every_required_field_has_a_synonym_list()
    {
        foreach (var kind in Enum.GetValues<ImportKind>())
        {
            Assert.All(ImportSchema.Fields(kind).Where(f => f.Required), f => Assert.NotEmpty(f.Synonyms));
        }
    }
}

public class ImportTemplateTests
{
    [Theory]
    [InlineData(ImportKind.Vehicles)]
    [InlineData(ImportKind.Companies)]
    [InlineData(ImportKind.Cargos)]
    [InlineData(ImportKind.Drivers)]
    [InlineData(ImportKind.Weighings)]
    public void The_template_for_every_kind_is_understood_by_the_importer_itself(ImportKind kind)
    {
        var table = CsvParser.ToTable(CsvParser.Parse(ImportTemplates.Csv(kind)).Select(r => (IReadOnlyList<string>)r).ToList(), "t.csv");
        var mapping = ColumnMapping.Auto(kind, table.Headers);

        Assert.All(ImportSchema.Fields(kind), f => Assert.True(mapping.IsMapped(f.Key), $"{kind}: «{f.Title}» not recognised in its own template"));
        Assert.Single(table.Rows);
    }

    [Fact]
    public void Csv_export_neutralises_formulas_but_keeps_numbers()
    {
        var csv = Csv.Build(["a"], [["=HYPERLINK(\"http://x\")"], ["+cmd"], ["@SUM(A1)"], ["-5"], ["28460"], ["обычный текст"]]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("\"'=HYPERLINK", lines[1]);
        Assert.Equal("'+cmd", lines[2]);
        Assert.Equal("'@SUM(A1)", lines[3]);
        Assert.Equal("-5", lines[4]);
        Assert.Equal("28460", lines[5]);
        Assert.Equal("обычный текст", lines[6]);
    }
}

public class FileTableReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weigh-read-" + Guid.NewGuid().ToString("N"));

    public FileTableReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Reads_utf8_csv_with_and_without_bom()
    {
        var text = "Номер;Груз\nА123ВС197;Щебень\n";
        var withBom = FileTableReader.Read(Write("a.csv", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)]));
        var without = FileTableReader.Read(Write("b.csv", Encoding.UTF8.GetBytes(text)));

        Assert.Equal(["Номер", "Груз"], withBom.Headers);
        Assert.Equal(["А123ВС197", "Щебень"], withBom.Rows[0]);
        Assert.Equal(withBom.Rows[0], without.Rows[0]);
        Assert.Equal("a.csv", withBom.SourceName);
    }

    [Fact]
    public void Reads_windows_1251_csv_the_default_of_old_russian_programs()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var table = FileTableReader.Read(Write("old.csv", Encoding.GetEncoding(1251).GetBytes("Номер;Контрагент\nА123ВС197;ООО «Север»\n")));

        Assert.Equal(["Номер", "Контрагент"], table.Headers);
        Assert.Equal("ООО «Север»", table.Rows[0][1]);
    }

    [Fact]
    public void Reads_utf16_csv_saved_by_excel_as_unicode_text()
    {
        var table = FileTableReader.Read(Write("u16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Номер\tГруз\nА1\tПесок\n")]));
        Assert.Equal(["Номер", "Груз"], table.Headers);
        Assert.Equal("Песок", table.Rows[0][1]);
    }

    [Fact]
    public void Reads_xlsx_dates_numbers_and_text_as_strings_in_invariant_form()
    {
        var path = Path.Combine(_dir, "journal.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Журнал");
            ws.Cell(1, 1).Value = "Дата";
            ws.Cell(1, 2).Value = "Номер";
            ws.Cell(1, 3).Value = "Брутто, кг";
            ws.Cell(1, 4).Value = "Время";
            ws.Cell(2, 1).Value = new DateTime(2026, 10, 28, 14, 32, 0);
            ws.Cell(2, 2).Value = "А123ВС197";
            ws.Cell(2, 3).Value = 42780;
            ws.Cell(2, 4).Value = new TimeSpan(14, 32, 0);
            ws.Cell(3, 1).Value = new DateTime(2026, 10, 29);
            ws.Cell(3, 2).Value = "К507МР50";
            ws.Cell(3, 3).Value = 42.78;
            wb.SaveAs(path);
        }

        var table = FileTableReader.Read(path);

        Assert.Equal(["Дата", "Номер", "Брутто, кг", "Время"], table.Headers);
        Assert.Equal(["2026-10-28 14:32:00", "А123ВС197", "42780", "14:32:00"], table.Rows[0]);
        Assert.Equal(["2026-10-29", "К507МР50", "42.78", ""], table.Rows[1]);
    }

    [Fact]
    public void Skips_an_empty_first_sheet_and_reads_the_first_one_with_data()
    {
        var path = Path.Combine(_dir, "two.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Пустой");
            var ws = wb.AddWorksheet("Данные");
            ws.Cell(1, 1).Value = "Название";
            ws.Cell(2, 1).Value = "Щебень";
            wb.SaveAs(path);
        }

        var table = FileTableReader.Read(path);

        Assert.Equal(["Название"], table.Headers);
        Assert.Equal("Щебень", table.Rows[0][0]);
    }

    [Fact]
    public void A_workbook_without_data_is_reported()
    {
        var path = Path.Combine(_dir, "empty.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Пустой");
            wb.SaveAs(path);
        }

        Assert.Contains("нет ни одного листа", Assert.Throws<ValidationException>(() => FileTableReader.Read(path)).Message);
    }

    [Fact]
    public void Reads_a_file_that_excel_still_has_open()
    {
        var path = Path.Combine(_dir, "locked.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("S").Cell(1, 1).Value = "Название";
            wb.SaveAs(path);
        }

        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal(["Название"], FileTableReader.Read(path).Headers);
    }

    [Theory]
    [InlineData("data.pdf")]
    [InlineData("data.docx")]
    [InlineData("data")]
    public void Unsupported_extensions_are_refused_with_a_hint(string name)
    {
        var ex = Assert.Throws<ValidationException>(() => FileTableReader.Read(Write(name, [1, 2, 3])));
        Assert.Contains(".xlsx", ex.Message);
    }

    [Fact]
    public void Garbage_with_an_excel_extension_gives_a_readable_error_not_a_crash()
    {
        var ex = Assert.Throws<ValidationException>(() => FileTableReader.Read(Write("fake.xlsx", Encoding.UTF8.GetBytes("this is not a spreadsheet"))));
        Assert.StartsWith("Не удалось прочитать файл", ex.Message);
    }

    [Fact]
    public void Missing_file_and_empty_csv_are_reported()
    {
        Assert.Contains("не найден", Assert.Throws<ValidationException>(() => FileTableReader.Read(Path.Combine(_dir, "nope.csv"))).Message);
        Assert.Contains("пуст", Assert.Throws<ValidationException>(() => FileTableReader.Read(Write("empty.csv", []))).Message);
    }

    [Fact]
    public async Task The_async_reader_does_the_same()
    {
        var table = await new FileTableReader().ReadAsync(Write("a.csv", Encoding.UTF8.GetBytes("Номер\nА1\n")));
        Assert.Equal("А1", table.Rows[0][0]);
    }
}

public class ImportServiceTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly ImportService _service;
    private readonly User _admin;

    public ImportServiceTests()
    {
        _service = new ImportService(_app.Vehicles, _app.Companies, _app.Cargos, _app.Drivers, _app.RealRepo, _app.Audit, _app.Time);
        _admin = _app.SignInAs(UserRole.Administrator);
    }

    public void Dispose() => _app.Dispose();

    private static TableData Table(string[] headers, params string[][] rows) =>
        new(headers, rows.Select(r => (IReadOnlyList<string>)r).ToList(), "test.csv");

    private Task<ImportPlan> Plan(ImportKind kind, TableData table, ImportOptions? options = null) =>
        _service.PlanAsync(kind, table, ColumnMapping.Auto(kind, table.Headers), options ?? new ImportOptions());

    // ---- planning: general ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_required_columns_are_named()
    {
        var table = Table(["Груз"], ["Щебень"]);
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Plan(ImportKind.Vehicles, table));
        Assert.Contains("«Госномер»", ex.Message);
    }

    [Fact]
    public async Task A_huge_file_is_refused_before_any_work()
    {
        var rows = Enumerable.Range(0, 200_001).Select(i => (IReadOnlyList<string>)[i.ToString()]).ToList();
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.PlanAsync(ImportKind.Cargos, new TableData(["Название"], rows, "big.csv"), ColumnMapping.Auto(ImportKind.Cargos, ["Название"]), new ImportOptions()));
        Assert.Contains("больше допустимых", ex.Message);
    }

    [Fact]
    public async Task Blank_rows_are_ignored_and_row_numbers_match_excel()
    {
        var plan = await Plan(ImportKind.Cargos, Table(["Название"], ["Щебень"], [""], ["Песок"]));

        Assert.Equal([2, 4], plan.Rows.Select(r => r.RowNumber));
    }

    // ---- vehicles --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Vehicles_are_normalised_and_masses_parsed()
    {
        var plan = await Plan(ImportKind.Vehicles, Table(
            ["Госномер", "Прицеп", "Контрагент", "Тара", "Допустимая масса"],
            ["a123bc 197", "ab 4567 77", "ООО «Север»", "14 320", "44 000"]));

        var row = Assert.Single(plan.Rows);
        Assert.Equal(ImportAction.Insert, row.Action);
        var v = Assert.IsType<Vehicle>(row.Payload);
        Assert.Equal("А123ВС197", v.Plate);
        Assert.Equal("АВ456777", v.Trailer);
        Assert.Equal(14320, v.TareKg);
        Assert.Equal(44000, v.MaxMassKg);
        Assert.Equal(1, plan.ToInsert);
        Assert.Equal(0, plan.Errors);
    }

    [Fact]
    public async Task Tonnes_are_converted_when_the_file_is_in_tonnes()
    {
        var plan = await Plan(ImportKind.Vehicles, Table(["Госномер", "Тара"], ["А123ВС197", "14,32"]), new ImportOptions { MassUnit = MassUnit.Tonnes });
        Assert.Equal(14320, ((Vehicle)plan.Rows[0].Payload!).TareKg);
    }

    [Fact]
    public async Task Bad_rows_become_errors_with_reasons_and_good_rows_still_go_through()
    {
        var plan = await Plan(ImportKind.Vehicles, Table(
            ["Госномер", "Тара", "Допустимая масса"],
            ["А111АА77", "", ""],
            ["", "1000", ""],
            ["В222ВВ77", "много", ""],
            ["Е333ЕЕ77", "", "ноль"],
            ["К444КК77", "20 000", "15 000"]));

        Assert.Equal([ImportAction.Insert, ImportAction.Skip, ImportAction.Skip, ImportAction.Skip, ImportAction.Insert], plan.Rows.Select(r => r.Action));
        Assert.Equal(3, plan.Errors);
        Assert.Contains("Не указан госномер", plan.Rows[1].Message);
        Assert.Contains("«много»", plan.Rows[2].Message);
        Assert.Contains("Допустимая масса «ноль»", plan.Rows[3].Message);
        Assert.Equal(RowSeverity.Warning, plan.Rows[4].Severity); // tare >= permitted mass: suspicious but importable
        Assert.Equal(2, plan.ToInsert);
        Assert.Equal(1, plan.Warnings);
    }

    [Fact]
    public async Task A_plate_repeated_in_the_file_is_skipped_with_the_first_row_named()
    {
        var plan = await Plan(ImportKind.Vehicles, Table(["Госномер"], ["А111АА77"], ["a111aa 77"]));

        Assert.Equal(ImportAction.Insert, plan.Rows[0].Action);
        Assert.Equal(ImportAction.Skip, plan.Rows[1].Action);
        Assert.Contains("строке 2", plan.Rows[1].Message);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public async Task Existing_vehicles_are_left_alone_by_default_and_filled_in_on_request()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77", Driver = "Старый водитель", MaxMassKg = 40000 });
        var table = Table(["Госномер", "Водитель", "Контрагент", "Допустимая масса"], ["А111АА77", "Новый водитель", "Север", ""]);

        var skip = await Plan(ImportKind.Vehicles, table);
        Assert.Equal(ImportAction.Skip, skip.Rows[0].Action);
        Assert.Contains("Уже есть", skip.Rows[0].Message);

        var update = await Plan(ImportKind.Vehicles, table, new ImportOptions { UpdateExisting = true });
        var merged = Assert.IsType<Vehicle>(update.Rows[0].Payload);
        Assert.Equal(ImportAction.Update, update.Rows[0].Action);
        Assert.Equal("Новый водитель", merged.Driver);
        Assert.Equal("Север", merged.Company);
        Assert.Equal(40000, merged.MaxMassKg); // an empty cell never erases what is there
    }

    [Fact]
    public async Task Update_with_nothing_new_is_reported_as_unchanged()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77", Driver = "Петров" });
        var plan = await Plan(ImportKind.Vehicles, Table(["Госномер", "Водитель"], ["А111АА77", "Петров"]), new ImportOptions { UpdateExisting = true });
        Assert.Equal(ImportAction.Skip, plan.Rows[0].Action);
        Assert.Equal("Без изменений.", plan.Rows[0].Message);
    }

    // ---- companies, cargo, drivers -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Companies_check_the_inn_and_skip_existing_names_in_any_case()
    {
        await _app.Companies.SaveAsync(new Company { Name = "ООО «Север»" });
        var plan = await Plan(ImportKind.Companies, Table(
            ["Название", "ИНН"],
            ["ооо «север»", "7700000000"],
            ["ИП Смирнов", "123"],
            ["АО «Юг»", "770000000000"],
            ["АО «ЮГ»", ""]));

        Assert.Equal([ImportAction.Skip, ImportAction.Insert, ImportAction.Insert, ImportAction.Skip], plan.Rows.Select(r => r.Action));
        Assert.Equal(RowSeverity.Warning, plan.Rows[1].Severity);
        Assert.Contains("10 или 12 цифр", plan.Rows[1].Message);
        Assert.Equal(RowSeverity.Ok, plan.Rows[2].Severity);
        Assert.Contains("Повтор", plan.Rows[3].Message);
    }

    [Fact]
    public async Task Company_update_fills_in_inn_and_note()
    {
        await _app.Companies.SaveAsync(new Company { Name = "ООО «Север»" });
        var plan = await Plan(ImportKind.Companies, Table(["Название", "ИНН", "Примечание"], ["ООО «Север»", "7700000000", "постоянный"]), new ImportOptions { UpdateExisting = true });

        var merged = Assert.IsType<Company>(plan.Rows[0].Payload);
        Assert.Equal(ImportAction.Update, plan.Rows[0].Action);
        Assert.Equal("7700000000", merged.Inn);
        Assert.Equal("постоянный", merged.Note);
    }

    [Fact]
    public async Task Cargo_and_drivers_import_by_name()
    {
        await _app.Cargos.SaveAsync(new CargoType { Name = "Щебень" });
        var cargo = await Plan(ImportKind.Cargos, Table(["Груз"], ["щебень"], ["Песок"], [""], ["  "]));
        Assert.Equal([ImportAction.Skip, ImportAction.Insert], cargo.Rows.Select(r => r.Action));
        Assert.Equal(0, cargo.Errors); // blank rows are simply ignored

        await _app.Drivers.SaveAsync(new Driver { FullName = "Петров С.И." });
        var drivers = await Plan(ImportKind.Drivers, Table(["ФИО", "Телефон"], ["Петров С.И.", "+7 900"], ["Сидоров"]), new ImportOptions { UpdateExisting = true });
        Assert.Equal(ImportAction.Update, drivers.Rows[0].Action);
        Assert.Equal("+7 900", ((Driver)drivers.Rows[0].Payload!).Phone);
        Assert.Equal(ImportAction.Insert, drivers.Rows[1].Action);
    }

    // ---- weighings -----------------------------------------------------------------------------------------------------------

    private static readonly string[] WeighingHeaders = ["Дата", "Госномер", "Контрагент", "Груз", "Брутто", "Тара", "Нетто"];

    [Fact]
    public async Task A_gross_weighing_computes_net_from_gross_and_tare()
    {
        var plan = await Plan(ImportKind.Weighings, Table(WeighingHeaders, ["28.10.2026 14:32", "a123bc197", "Север", "Щебень", "42 780", "14 320", ""]));

        var w = Assert.IsType<Weighing>(Assert.Single(plan.Rows).Payload);
        Assert.Equal("А123ВС197", w.Plate);
        Assert.Equal(WeighingKind.Gross, w.Kind);
        Assert.Equal(42780, w.GrossKg);
        Assert.Equal(14320, w.TareKg);
        Assert.Equal(28460, w.NetKg);
        Assert.Equal(WeighingStatus.Completed, w.Status);
        Assert.Equal(new DateTime(2026, 10, 28, 14, 32, 0), w.Time.LocalDateTime);
    }

    [Fact]
    public async Task Tare_is_derived_from_gross_and_net_when_the_file_has_no_tare_column()
    {
        var plan = await Plan(ImportKind.Weighings, Table(["Дата", "Номер", "Брутто", "Нетто"], ["28.10.2026 14:32", "А123ВС197", "42780", "28460"]));
        var w = (Weighing)plan.Rows[0].Payload!;
        Assert.Equal(14320, w.TareKg);
        Assert.Equal(28460, w.NetKg);
    }

    [Fact]
    public async Task A_net_that_contradicts_gross_minus_tare_is_corrected_with_a_warning()
    {
        var plan = await Plan(ImportKind.Weighings, Table(WeighingHeaders, ["28.10.2026 14:32", "А123ВС197", "", "", "42780", "14320", "30000"]));

        var row = plan.Rows[0];
        Assert.Equal(RowSeverity.Warning, row.Severity);
        Assert.Equal(ImportAction.Insert, row.Action);
        Assert.Contains("использовано 28", row.Message);
        Assert.Equal(28460, ((Weighing)row.Payload!).NetKg);
    }

    [Fact]
    public async Task Tare_weighings_are_recognised_by_the_operation_column_or_by_having_only_a_tare()
    {
        var withKind = await Plan(ImportKind.Weighings, Table(["Дата", "Номер", "Операция", "Тара"], ["28.10.2026 10:00", "А111АА77", "Тара", "14 000"]));
        var tare = (Weighing)withKind.Rows[0].Payload!;
        Assert.Equal(WeighingKind.Tare, tare.Kind);
        Assert.Equal(14000, tare.TareKg);
        Assert.Null(tare.GrossKg);
        Assert.Null(tare.NetKg);

        var implicitTare = await Plan(ImportKind.Weighings, Table(["Дата", "Номер", "Тара"], ["28.10.2026 10:00", "А111АА77", "14000"]));
        Assert.Equal(WeighingKind.Tare, ((Weighing)implicitTare.Rows[0].Payload!).Kind);
    }

    [Theory]
    [InlineData("28.10.2026 14:32", "А123ВС197", "", "", "не указана масса брутто")]
    [InlineData("28.10.2026 14:32", "А123ВС197", "42780", "42780", "не меньше брутто")]
    [InlineData("28.10.2026 14:32", "А123ВС197", "42780", "50000", "не меньше брутто")]
    [InlineData("вчера", "А123ВС197", "42780", "", "Дата «вчера» не распознана")]
    [InlineData("28.10.2026 14:32", "", "42780", "", "Не указан госномер")]
    [InlineData("28.10.2026 14:32", "А123ВС197", "abc", "", "Брутто «abc»")]
    public async Task Invalid_weighings_are_reported_with_the_reason(string date, string plate, string gross, string tare, string expected)
    {
        var plan = await Plan(ImportKind.Weighings, Table(["Дата", "Госномер", "Брутто", "Тара"], [date, plate, gross, tare]));

        var row = Assert.Single(plan.Rows);
        Assert.Equal(RowSeverity.Error, row.Severity);
        Assert.Equal(ImportAction.Skip, row.Action);
        Assert.Contains(expected, row.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, plan.Errors);
    }

    [Fact]
    public async Task Date_and_time_in_separate_columns_are_combined()
    {
        var plan = await Plan(ImportKind.Weighings, Table(["Дата", "Время", "Номер", "Брутто"], ["28.10.2026", "14:32", "А123ВС197", "42780"]));
        Assert.Equal(new DateTime(2026, 10, 28, 14, 32, 0), ((Weighing)plan.Rows[0].Payload!).Time.LocalDateTime);
    }

    [Fact]
    public async Task Dates_far_in_the_future_are_flagged_as_a_probable_format_mix_up()
    {
        var plan = await Plan(ImportKind.Weighings, Table(["Дата", "Номер", "Брутто"], ["28.10.2030 14:32", "А123ВС197", "42780"]));
        Assert.Equal(RowSeverity.Warning, plan.Rows[0].Severity);
        Assert.Contains("в будущем", plan.Rows[0].Message);
        Assert.Equal(ImportAction.Insert, plan.Rows[0].Action);
    }

    [Fact]
    public async Task Rows_repeated_in_the_file_or_already_in_the_journal_are_skipped_so_importing_twice_is_safe()
    {
        await _app.AddWeighing("А123ВС197", time: new DateTimeOffset(2026, 10, 28, 14, 32, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28))), net: 28000);
        var rows = new[]
        {
            new[] { "28.10.2026 14:32", "А123ВС197", "42000", "14000" }, // same minute, plate, gross, tare as the stored one
            ["28.10.2026 15:10", "К507МР50", "38940", "13800"],
            ["28.10.2026 15:10", "к507мр50", "38940", "13800"],         // repeated in the file
        };
        var plan = await Plan(ImportKind.Weighings, Table(["Дата", "Госномер", "Брутто", "Тара"], rows));

        Assert.Equal([ImportAction.Skip, ImportAction.Insert, ImportAction.Skip], plan.Rows.Select(r => r.Action));
        Assert.Contains("уже есть", plan.Rows[0].Message);
        Assert.Equal(1, plan.ToInsert);
        Assert.Equal(2, plan.Skipped);
    }

    // ---- applying ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Applying_a_vehicle_plan_writes_inserts_and_updates_and_audits_it()
    {
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "А111АА77", Driver = "Старый" });
        var plan = await Plan(ImportKind.Vehicles,
            Table(["Госномер", "Водитель"], ["А111АА77", "Новый"], ["В222ВВ77", "Другой"], ["", "x"]),
            new ImportOptions { UpdateExisting = true });

        var outcome = await _service.ApplyAsync(plan, _admin);

        Assert.Equal(new ImportOutcome(1, 1, 0, 1), outcome);
        var all = await _app.Vehicles.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("Новый", all.Single(v => v.Plate == "А111АА77").Driver);
        Assert.Equal("Другой", all.Single(v => v.Plate == "В222ВВ77").Driver);
        var entry = (await _app.Audit.GetRecentAsync(1)).Single();
        Assert.Equal("import", entry.Action);
        Assert.Contains("добавлено 1, обновлено 1, пропущено 0, с ошибками 1", entry.Details);
    }

    [Fact]
    public async Task Applying_other_reference_plans()
    {
        await _service.ApplyAsync(await Plan(ImportKind.Companies, Table(["Название", "ИНН"], ["ООО «Север»", "7700000000"])), _admin);
        await _service.ApplyAsync(await Plan(ImportKind.Cargos, Table(["Название"], ["Щебень"], ["Песок"])), _admin);
        await _service.ApplyAsync(await Plan(ImportKind.Drivers, Table(["ФИО", "Телефон"], ["Петров", "+7"])), _admin);

        Assert.Equal("7700000000", (await _app.Companies.GetAllAsync()).Single().Inn);
        Assert.Equal(["Песок", "Щебень"], (await _app.Cargos.GetAllAsync()).Select(c => c.Name));
        Assert.Equal("+7", (await _app.Drivers.GetAllAsync()).Single().Phone);
    }

    [Fact]
    public async Task Applying_a_weighing_plan_stores_rows_with_history_and_the_tare_becomes_known()
    {
        var plan = await Plan(ImportKind.Weighings, Table(
            ["Дата", "Номер", "Операция", "Брутто", "Тара"],
            ["27.10.2026 09:00", "А123ВС197", "Тара", "", "14 320"],
            ["28.10.2026 14:32", "А123ВС197", "Брутто", "42 780", ""]));

        var outcome = await _service.ApplyAsync(plan, _admin);

        Assert.Equal(2, outcome.Inserted);
        var stored = (await _app.RealRepo.QueryAsync(new WeighingQuery())).OrderBy(w => w.Time).ToList();
        Assert.Equal([WeighingKind.Tare, WeighingKind.Gross], stored.Select(w => w.Kind));
        var history = Assert.Single(await _app.RealRepo.GetHistoryAsync(stored[0].Id));
        Assert.Equal("Импортировано из файла «test.csv»", history.Text);
        Assert.Equal("Тестов А.А.", history.User);
        Assert.Equal(14320, await _app.RealRepo.FindTareKgAsync("А123ВС197")); // imported tare feeds the net of future weighings
    }

    [Fact]
    public async Task Importing_the_same_file_twice_adds_nothing_the_second_time()
    {
        var table = Table(["Дата", "Номер", "Брутто", "Тара"], ["28.10.2026 14:32", "А123ВС197", "42780", "14320"]);
        await _service.ApplyAsync(await Plan(ImportKind.Weighings, table), _admin);

        var second = await Plan(ImportKind.Weighings, table);

        Assert.False(second.HasWork);
        Assert.Equal(1, second.Skipped);
        Assert.Single(await _app.RealRepo.QueryAsync(new WeighingQuery()));
    }

    [Fact]
    public async Task Only_active_administrators_may_apply_a_plan()
    {
        var plan = await Plan(ImportKind.Cargos, Table(["Название"], ["Щебень"]));

        foreach (var role in new[] { UserRole.Operator, UserRole.Manager })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ApplyAsync(plan, _admin with { Role = role }));
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ApplyAsync(plan, _admin with { IsActive = false }));
        Assert.Empty(await _app.Cargos.GetAllAsync());
    }

    [Fact]
    public async Task A_failing_write_rolls_everything_back()
    {
        // Two rows with the same new plate can't get past planning, so sneak a clash in afterwards: the second insert must undo the first.
        var plan = await Plan(ImportKind.Vehicles, Table(["Госномер"], ["А111АА77"], ["В222ВВ77"]));
        await _app.Vehicles.SaveAsync(new Vehicle { Plate = "В222ВВ77" }); // appears between planning and applying

        await Assert.ThrowsAsync<DuplicateException>(() => _service.ApplyAsync(plan, _admin));

        Assert.Equal(["В222ВВ77"], (await _app.Vehicles.GetAllAsync()).Select(v => v.Plate));
    }

    [Fact]
    public async Task Progress_is_reported_for_big_imports()
    {
        var rows = Enumerable.Range(0, 1200).Select(i => new[] { $"Груз {i}" }).ToArray();
        var plan = await Plan(ImportKind.Cargos, Table(["Название"], rows));
        var reports = new List<int>();

        await _service.ApplyAsync(plan, _admin, new SyncProgress(reports.Add));

        Assert.Equal(1200, (await _app.Cargos.GetAllAsync()).Count);
        Assert.Equal(1200, reports[^1]);
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    [Fact]
    public async Task A_realistic_migration_file_end_to_end_through_a_real_xlsx()
    {
        var path = Path.Combine(_app.Dir, "migration.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Взвешивания");
            string[] headers = ["Дата", "Время", "Гос. номер", "Контрагент", "Груз", "Масса брутто, кг", "Масса тары, кг", "Водитель"];
            for (var c = 0; c < headers.Length; c++)
            {
                ws.Cell(1, c + 1).Value = headers[c];
            }

            ws.Cell(2, 1).Value = new DateTime(2026, 10, 27);
            ws.Cell(2, 2).Value = new TimeSpan(9, 15, 0);
            ws.Cell(2, 3).Value = "А123ВС197";
            ws.Cell(2, 4).Value = "ООО «Север»";
            ws.Cell(2, 5).Value = "Щебень";
            ws.Cell(2, 6).Value = 42780;
            ws.Cell(2, 7).Value = 14320;
            ws.Cell(2, 8).Value = "Петров С.И.";
            ws.Cell(3, 1).Value = new DateTime(2026, 10, 27);
            ws.Cell(3, 2).Value = new TimeSpan(10, 40, 0);
            ws.Cell(3, 3).Value = "k507mp50"; // latin letters, lower case
            ws.Cell(3, 4).Value = "ИП Смирнов";
            ws.Cell(3, 5).Value = "Песок";
            ws.Cell(3, 6).Value = "38 945";
            ws.Cell(3, 7).Value = "13 800";
            ws.Cell(4, 1).Value = "не дата";
            ws.Cell(4, 3).Value = "Е412КХ197";
            ws.Cell(4, 6).Value = 44060;
            wb.SaveAs(path);
        }

        var table = await new FileTableReader().ReadAsync(path);
        var mapping = ColumnMapping.Auto(ImportKind.Weighings, table.Headers);
        Assert.All(new[] { "date", "time", "plate", "company", "cargo", "gross", "tare", "driver" }, f => Assert.True(mapping.IsMapped(f), f));

        var plan = await _service.PlanAsync(ImportKind.Weighings, table, mapping, new ImportOptions());
        Assert.Equal(2, plan.ToInsert);
        Assert.Equal(1, plan.Errors);
        Assert.Contains("Дата «не дата» не распознана", plan.Rows[2].Message);

        await _service.ApplyAsync(plan, _admin);

        var stored = (await _app.RealRepo.QueryAsync(new WeighingQuery())).OrderBy(w => w.Time).ToList();
        Assert.Equal(["А123ВС197", "К507МР50"], stored.Select(w => w.Plate));
        Assert.Equal(new DateTime(2026, 10, 27, 9, 15, 0), stored[0].Time.LocalDateTime);
        Assert.Equal([28460, 25145], stored.Select(w => w.NetKg!.Value));
        Assert.Equal("Петров С.И.", stored[0].Driver);
    }
}
