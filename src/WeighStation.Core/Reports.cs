using System.Globalization;
using System.Text;
using WeighStation.Core.Models;

namespace WeighStation.Core;

public enum ReportGrouping { Company, Cargo, Vehicle, Day }

public sealed record ReportRow(string Key, int Weighings, long NetKg)
{
    public long AverageNetKg => Weighings == 0 ? 0 : NetKg / Weighings;
}

public sealed record Report(
    ReportGrouping Grouping,
    IReadOnlyList<ReportRow> Rows,
    int TotalWeighings,
    long TotalNetKg,
    /// <summary>Gross weighings whose net is unknown because the vehicle has no tare.</summary>
    int WithoutNet);

public static class ReportBuilder
{
    public const string Unknown = "(не указан)";

    public static Report Build(IEnumerable<Weighing> weighings, ReportGrouping grouping)
    {
        var valid = weighings.Where(w => w.Status != WeighingStatus.Cancelled).ToList();
        var groups = valid
            .GroupBy(w => KeyOf(w, grouping))
            .Select(g => (g.Key.Sort, Row: new ReportRow(g.Key.Label, g.Count(), g.Sum(w => (long)(w.NetKg ?? 0)))));

        var ordered = (grouping == ReportGrouping.Day
                ? groups.OrderBy(g => g.Sort)
                : groups.OrderByDescending(g => g.Row.NetKg).ThenBy(g => g.Row.Key, StringComparer.CurrentCultureIgnoreCase))
            .Select(g => g.Row)
            .ToList();

        return new Report(
            grouping,
            ordered,
            valid.Count,
            valid.Sum(w => (long)(w.NetKg ?? 0)),
            valid.Count(w => w.Kind == WeighingKind.Gross && w.NetKg is null));
    }

    public static string ToCsv(Report report) => Csv.Build(
        ["Группа", "Взвешиваний", "Нетто, кг", "Среднее нетто, кг"],
        report.Rows.Select(r => (IReadOnlyList<string>)[r.Key, Num(r.Weighings), Num(r.NetKg), Num(r.AverageNetKg)])
            .Append(["Итого", Num(report.TotalWeighings), Num(report.TotalNetKg), ""]));

    private static (string Label, DateTime Sort) KeyOf(Weighing w, ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.Company => (Or(w.Counterparty), default),
        ReportGrouping.Cargo => (Or(w.Cargo), default),
        ReportGrouping.Vehicle => (PlateNumber.Format(w.Plate), default),
        _ => (w.Time.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), w.Time.ToLocalTime().Date),
    };

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim();

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Semicolon-separated values: what Russian-locale Excel opens by double click.</summary>
public static class Csv
{
    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        AppendLine(sb, headers);
        foreach (var row in rows)
        {
            AppendLine(sb, row);
        }

        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }

            sb.Append(Escape(cells[i]));
        }

        sb.Append("\r\n");
    }

    private static string Escape(string cell)
    {
        // CSV injection: a text cell starting with = + - @ is run as a formula when the file is opened in Excel.
        // Imported data comes from outside, so such cells get a leading apostrophe (shown by Excel as plain text).
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsNumber(cell))
        {
            cell = "'" + cell;
        }

        return cell.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
    }

    private static bool IsNumber(string cell) =>
        double.TryParse(cell, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
}
