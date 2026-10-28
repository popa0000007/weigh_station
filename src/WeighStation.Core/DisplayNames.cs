using System.Globalization;
using WeighStation.Core.Models;

namespace WeighStation.Core;

/// <summary>Russian captions for domain values, shared by the journal, exports and tickets.</summary>
public static class DisplayNames
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Of(WeighingKind kind) => kind == WeighingKind.Gross ? "Брутто" : "Тара";

    public static string Of(WeighingStatus status) => status switch
    {
        WeighingStatus.Completed => "Завершено",
        WeighingStatus.ManualEdit => "Ручная правка",
        WeighingStatus.Cancelled => "Отменено",
        _ => status.ToString(),
    };

    /// <summary>"42 780" with a non-breaking space, or an em dash for missing values.</summary>
    public static string Kg(long? kg) => kg is null ? "—" : kg.Value.ToString("N0", Ru);

    public static string DateTime(DateTimeOffset time) => time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru);
}

public static class JournalExport
{
    public static string ToCsv(IEnumerable<Weighing> weighings) => Csv.Build(
        ["№", "Дата", "Время", "Номер", "Прицеп", "Водитель", "Контрагент", "Груз", "Операция", "Брутто, кг", "Тара, кг", "Нетто, кг", "Статус", "Оператор"],
        weighings.Select(w => (IReadOnlyList<string>)
        [
            w.Id.ToString(CultureInfo.InvariantCulture),
            w.Time.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            w.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            PlateNumber.Format(w.Plate),
            PlateNumber.Format(w.Trailer),
            w.Driver ?? "",
            w.Counterparty ?? "",
            w.Cargo ?? "",
            DisplayNames.Of(w.Kind),
            w.GrossKg?.ToString(CultureInfo.InvariantCulture) ?? "",
            w.TareKg?.ToString(CultureInfo.InvariantCulture) ?? "",
            w.NetKg?.ToString(CultureInfo.InvariantCulture) ?? "",
            DisplayNames.Of(w.Status),
            w.Operator ?? "",
        ]));
}
