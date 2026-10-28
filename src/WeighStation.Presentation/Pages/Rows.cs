using WeighStation.Core;
using WeighStation.Core.Models;

namespace WeighStation.Presentation.Pages;

/// <summary>A weighing prepared for display (formatted once, bound many times).</summary>
public sealed record JournalRow(
    long Id,
    string Date,
    string Time,
    string Plate,
    string Trailer,
    string Driver,
    string Company,
    string Cargo,
    string Kind,
    string Gross,
    string Tare,
    string Net,
    string Status,
    bool IsCancelled,
    Weighing Source)
{
    public static JournalRow From(Weighing w) => new(
        w.Id,
        w.Time.ToLocalTime().ToString("dd.MM.yyyy"),
        w.Time.ToLocalTime().ToString("HH:mm"),
        PlateNumber.Format(w.Plate),
        PlateNumber.Format(w.Trailer),
        w.Driver ?? "",
        w.Counterparty ?? "",
        w.Cargo ?? "",
        DisplayNames.Of(w.Kind),
        DisplayNames.Kg(w.GrossKg),
        DisplayNames.Kg(w.TareKg),
        DisplayNames.Kg(w.NetKg),
        DisplayNames.Of(w.Status),
        w.Status == WeighingStatus.Cancelled,
        w);
}

public sealed record HistoryRow(string Time, string User, string Text)
{
    public static HistoryRow From(AuditEntry e) => new(e.Time.ToLocalTime().ToString("dd.MM HH:mm:ss"), e.User, e.Text);
}
