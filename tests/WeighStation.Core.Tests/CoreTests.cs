using WeighStation.Core.Models;

namespace WeighStation.Core.Tests;

public class PlateNumberTests
{
    [Theory]
    [InlineData("А123ВС197", "А123ВС197")]
    [InlineData("a123bc197", "А123ВС197")]
    [InlineData("  А 123 ВС 197 ", "А123ВС197")]
    [InlineData("A123BC-197", "А123ВС197")]
    [InlineData("в777вв77", "В777ВВ77")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_gives_one_canonical_spelling(string? input, string expected) =>
        Assert.Equal(expected, PlateNumber.Normalize(input));

    [Fact]
    public void Normalize_keeps_latin_letters_that_have_no_cyrillic_twin() =>
        Assert.Equal("D123FG", PlateNumber.Normalize("d123fg"));

    [Theory]
    [InlineData("А123ВС197", "А123ВС 197")]
    [InlineData("a123bc77", "А123ВС 77")]
    [InlineData("АВ456777", "АВ4567 77")]
    [InlineData("ab 4567 777", "АВ4567 777")]
    [InlineData("ТРАКТОР", "ТРАКТОР")]
    public void Format_adds_the_space_before_the_region(string input, string expected) =>
        Assert.Equal(expected, PlateNumber.Format(input));
}

public class ReportTests
{
    private static Weighing W(string plate, string? company, string? cargo, int? net, int day,
        WeighingStatus status = WeighingStatus.Completed, WeighingKind kind = WeighingKind.Gross) => new()
    {
        Plate = plate,
        Counterparty = company,
        Cargo = cargo,
        NetKg = net,
        Kind = kind,
        Status = status,
        Time = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, day, 12, 0, 0))),
    };

    private static readonly Weighing[] Sample =
    [
        W("А111АА77", "Север", "Щебень", 28000, 1),
        W("А111АА77", "Север", "Песок", 25000, 1),
        W("В222ВВ77", "Юг", "Щебень", 30000, 2),
        W("В222ВВ77", null, null, null, 3),
        W("С333СС77", "Север", "Щебень", 99999, 3, WeighingStatus.Cancelled),
    ];

    [Fact]
    public void Groups_by_company_sorted_by_net_descending_and_skips_cancelled()
    {
        var report = ReportBuilder.Build(Sample, ReportGrouping.Company);

        Assert.Equal(["Север", "Юг", ReportBuilder.Unknown], report.Rows.Select(r => r.Key));
        Assert.Equal([2, 1, 1], report.Rows.Select(r => r.Weighings));
        Assert.Equal(53000, report.Rows[0].NetKg);
        Assert.Equal(26500, report.Rows[0].AverageNetKg);
        Assert.Equal(4, report.TotalWeighings);
        Assert.Equal(83000, report.TotalNetKg);
        Assert.Equal(1, report.WithoutNet);
    }

    [Fact]
    public void Groups_by_cargo_and_vehicle()
    {
        Assert.Equal(["Щебень", "Песок", ReportBuilder.Unknown],
            ReportBuilder.Build(Sample, ReportGrouping.Cargo).Rows.Select(r => r.Key));
        Assert.Equal(["А111АА 77", "В222ВВ 77"],
            ReportBuilder.Build(Sample, ReportGrouping.Vehicle).Rows.Select(r => r.Key));
    }

    [Fact]
    public void Groups_by_day_in_chronological_order()
    {
        var report = ReportBuilder.Build(Sample, ReportGrouping.Day);
        Assert.Equal(["01.10.2026", "02.10.2026", "03.10.2026"], report.Rows.Select(r => r.Key));
        Assert.Equal([2, 1, 1], report.Rows.Select(r => r.Weighings));
    }

    [Fact]
    public void Tare_weighings_count_as_weighings_but_not_as_missing_net()
    {
        var report = ReportBuilder.Build([W("А111АА77", "Север", null, null, 1, kind: WeighingKind.Tare)], ReportGrouping.Company);
        Assert.Equal(1, report.TotalWeighings);
        Assert.Equal(0, report.WithoutNet);
    }

    [Fact]
    public void Empty_input_gives_empty_report()
    {
        var report = ReportBuilder.Build([], ReportGrouping.Company);
        Assert.Empty(report.Rows);
        Assert.Equal(0, report.TotalNetKg);
    }

    [Fact]
    public void Report_csv_has_header_rows_and_totals()
    {
        var csv = ReportBuilder.ToCsv(ReportBuilder.Build(Sample, ReportGrouping.Company));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Группа;Взвешиваний;Нетто, кг;Среднее нетто, кг", lines[0]);
        Assert.Equal("Север;2;53000;26500", lines[1]);
        Assert.Equal("Итого;4;83000;", lines[^1]);
    }
}

public class CsvTests
{
    [Fact]
    public void Cells_with_separators_quotes_or_newlines_are_quoted()
    {
        var csv = Csv.Build(["a", "b"], [["x;y", "say \"hi\""], ["line1\nline2", "plain"]]);
        Assert.Equal("a;b\r\n\"x;y\";\"say \"\"hi\"\"\"\r\n\"line1\nline2\";plain\r\n", csv);
    }

    [Fact]
    public void Journal_export_contains_formatted_plate_and_russian_labels()
    {
        var csv = JournalExport.ToCsv([new Weighing
        {
            Id = 7,
            Plate = "А123ВС197",
            Counterparty = "ООО «Север»; филиал",
            Kind = WeighingKind.Gross,
            GrossKg = 42780,
            TareKg = 14320,
            NetKg = 28460,
            Status = WeighingStatus.ManualEdit,
            Time = DateTimeOffset.Now,
        }]);

        var row = csv.Split("\r\n")[1];
        Assert.StartsWith("7;", row);
        Assert.Contains(";А123ВС 197;", row);
        Assert.Contains("\"ООО «Север»; филиал\"", row);
        Assert.Contains(";Брутто;42780;14320;28460;Ручная правка;", row);
    }
}

public class DisplayNamesTests
{
    [Fact]
    public void Kg_groups_thousands_and_shows_dash_for_missing()
    {
        Assert.Equal("42 780", DisplayNames.Kg(42780));
        Assert.Equal("—", DisplayNames.Kg(null));
        Assert.Equal("1 284 060", DisplayNames.Kg(1_284_060L));
    }
}
