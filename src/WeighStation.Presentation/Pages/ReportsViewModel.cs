using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Security;
using WeighStation.Presentation.Services;

namespace WeighStation.Presentation.Pages;

public sealed record GroupingOption(string Label, ReportGrouping Value);

public sealed record ReportRowView(string Key, string Weighings, string Net, string Average, double Share);

/// <summary>Totals per company / cargo / vehicle / day for a period, exportable to CSV.</summary>
public sealed partial class ReportsViewModel : PageViewModel
{
    private readonly IWeighingRepository _repository;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly CurrentUser _user;
    private Report? _report;

    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private GroupingOption _grouping;
    [ObservableProperty] private string _totals = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _infoMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _hasRows;

    public ReportsViewModel(IWeighingRepository repository, IDialogService dialogs, CurrentUser user, TimeProvider time)
    {
        _repository = repository;
        _dialogs = dialogs;
        _user = user;
        _time = time;
        _user.Changed += (_, _) => ExportCommand.NotifyCanExecuteChanged();

        GroupingOptions =
        [
            new("По контрагентам", ReportGrouping.Company),
            new("По грузам", ReportGrouping.Cargo),
            new("По транспорту", ReportGrouping.Vehicle),
            new("По дням", ReportGrouping.Day),
        ];
        _grouping = GroupingOptions[0];

        var today = time.GetLocalNow().Date;
        _dateFrom = new DateTime(today.Year, today.Month, 1);
        _dateTo = today;
    }

    public override string Title => "Отчёты";

    public override string IconData => "M5 20V10M12 20V4M19 20v-7";

    public override Permission Required => Permission.ViewReports;

    public IReadOnlyList<GroupingOption> GroupingOptions { get; }

    public ObservableCollection<ReportRowView> Rows { get; } = [];

    public override Task ActivateAsync() => BuildAsync();

    [RelayCommand]
    private async Task BuildAsync()
    {
        ErrorMessage = InfoMessage = Warning = "";
        if (DateFrom is { } from && DateTo is { } to && from > to)
        {
            ErrorMessage = "Дата «с» позже даты «по».";
            return;
        }

        try
        {
            var found = await _repository.QueryAsync(new WeighingQuery
            {
                From = DateFrom is { } f ? Local(f) : null,
                To = DateTo is { } t ? Local(t.AddDays(1)) : null,
                Limit = int.MaxValue,
            });

            _report = ReportBuilder.Build(found, Grouping.Value);
            var max = _report.Rows.Count == 0 ? 0 : _report.Rows.Max(r => r.NetKg);
            Rows.Clear();
            foreach (var r in _report.Rows)
            {
                Rows.Add(new ReportRowView(
                    r.Key, r.Weighings.ToString(), DisplayNames.Kg(r.NetKg), DisplayNames.Kg(r.AverageNetKg),
                    max > 0 ? (double)r.NetKg / max : 0));
            }

            HasRows = Rows.Count > 0;
            Totals = $"Взвешиваний: {_report.TotalWeighings} · нетто: {DisplayNames.Kg(_report.TotalNetKg)} кг";
            Warning = _report.WithoutNet > 0
                ? $"У {_report.WithoutNet} взвешиваний нет нетто: для этих машин неизвестна тара. Взвесьте их порожними или укажите тару в справочнике."
                : "";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось построить отчёт: {ex.Message}";
        }
    }

    private bool CanExport => HasRows && _user.Can(Permission.ExportData);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        ErrorMessage = InfoMessage = "";
        if (_report is null || !_user.Can(Permission.ExportData))
        {
            return;
        }

        var path = _dialogs.PickSaveFile(
            "Экспорт отчёта", $"report-{_time.GetLocalNow():yyyy-MM-dd}.csv", "CSV (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, ReportBuilder.ToCsv(_report), new UTF8Encoding(true));
            InfoMessage = "Отчёт сохранён.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить файл: {ex.Message}";
        }
    }

    private DateTimeOffset Local(DateTime day) => new(day.Date, _time.LocalTimeZone.GetUtcOffset(day.Date));
}
