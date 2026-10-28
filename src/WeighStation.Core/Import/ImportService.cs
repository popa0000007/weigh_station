using System.Globalization;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Core.Security;

namespace WeighStation.Core.Import;

public enum ImportAction { Insert, Update, Skip }

public enum RowSeverity { Ok, Warning, Error }

/// <summary>One source row after analysis: what will happen to it and why. <see cref="Payload"/> is what gets stored.</summary>
public sealed record PlannedRow(int RowNumber, ImportAction Action, RowSeverity Severity, string Message, string Summary, object? Payload = null);

public sealed record ImportOptions
{
    /// <summary>Records that already exist: false = leave them alone, true = fill in the fields the file provides.</summary>
    public bool UpdateExisting { get; init; }
    public MassUnit MassUnit { get; init; } = MassUnit.Kilograms;
}

public sealed record ImportPlan(ImportKind Kind, string SourceName, IReadOnlyList<PlannedRow> Rows)
{
    public int ToInsert => Rows.Count(r => r.Action == ImportAction.Insert);
    public int ToUpdate => Rows.Count(r => r.Action == ImportAction.Update);
    public int Errors => Rows.Count(r => r.Severity == RowSeverity.Error);
    public int Warnings => Rows.Count(r => r.Severity == RowSeverity.Warning && r.Action != ImportAction.Skip);
    /// <summary>Valid rows that need no action (already present, repeated in the file).</summary>
    public int Skipped => Rows.Count(r => r.Action == ImportAction.Skip && r.Severity != RowSeverity.Error);
    public bool HasWork => ToInsert + ToUpdate > 0;
}

public sealed record ImportOutcome(int Inserted, int Updated, int Skipped, int Errors);

/// <summary>
/// Moves data from spreadsheets into the program: first <b>plan</b> (read the rows, check them, compare with what exists,
/// change nothing), then <b>apply</b> (write the plan in one transaction). The split lets the screen show a preview
/// and lets the user fix the column mapping before anything is written.
/// </summary>
public sealed class ImportService(
    IVehicleStore vehicles,
    IReferenceStore<Company> companies,
    IReferenceStore<CargoType> cargos,
    IReferenceStore<Driver> drivers,
    IWeighingRepository weighings,
    IAuditLog audit,
    TimeProvider time)
{
    private const int MaxRows = 200_000;

    public async Task<ImportPlan> PlanAsync(
        ImportKind kind, TableData table, ColumnMapping mapping, ImportOptions options, CancellationToken ct = default)
    {
        if (table.Rows.Count > MaxRows)
        {
            throw new ValidationException($"В файле {table.Rows.Count:N0} строк, это больше допустимых {MaxRows:N0}. Разбейте его на части.");
        }

        var missing = ImportSchema.Fields(kind).Where(f => f.Required && !mapping.IsMapped(f.Key)).Select(f => $"«{f.Title}»").ToList();
        if (missing.Count > 0)
        {
            throw new ValidationException($"Укажите колонки для обязательных полей: {string.Join(", ", missing)}.");
        }

        var ctx = new Context(table, mapping, options);
        var rows = kind switch
        {
            ImportKind.Vehicles => await PlanVehiclesAsync(ctx, ct),
            ImportKind.Companies => await PlanNamesAsync(ctx, "name", companies, c => c.Name, (name, row, existing) => PlanCompany(ctx, name, row, existing), ct),
            ImportKind.Cargos => await PlanNamesAsync(ctx, "name", cargos, c => c.Name, (name, row, existing) => PlanCargo(name, row, existing), ct),
            ImportKind.Drivers => await PlanNamesAsync(ctx, "name", drivers, d => d.FullName, (name, row, existing) => PlanDriver(ctx, name, row, existing), ct),
            ImportKind.Weighings => await PlanWeighingsAsync(ctx, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new ImportPlan(kind, table.SourceName, rows);
    }

    public async Task<ImportOutcome> ApplyAsync(
        ImportPlan plan, User actor, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!actor.IsActive || !RolePermissions.Has(actor.Role, Permission.ImportData))
        {
            throw new UnauthorizedAccessException("Недостаточно прав для импорта данных.");
        }

        var work = plan.Rows.Where(r => r.Action != ImportAction.Skip && r.Payload is not null).ToList();
        switch (plan.Kind)
        {
            case ImportKind.Vehicles:
                await vehicles.BulkSaveAsync(work.Select(r => (Vehicle)r.Payload!).ToList(), progress, ct);
                break;
            case ImportKind.Companies:
                await companies.BulkSaveAsync(work.Select(r => (Company)r.Payload!).ToList(), progress, ct);
                break;
            case ImportKind.Cargos:
                await cargos.BulkSaveAsync(work.Select(r => (CargoType)r.Payload!).ToList(), progress, ct);
                break;
            case ImportKind.Drivers:
                await drivers.BulkSaveAsync(work.Select(r => (Driver)r.Payload!).ToList(), progress, ct);
                break;
            case ImportKind.Weighings:
                var now = time.GetUtcNow();
                var entry = (Weighing w) => new AuditEntry(now, actor.DisplayName, $"Импортировано из файла «{plan.SourceName}»");
                await weighings.ImportAsync(work.Select(r => ((Weighing)r.Payload!, entry((Weighing)r.Payload!))).ToList(), progress, ct);
                break;
        }

        var outcome = new ImportOutcome(plan.ToInsert, plan.ToUpdate, plan.Skipped, plan.Errors);
        await audit.WriteAsync(
            actor.Login, "import",
            $"{ImportSchema.Title(plan.Kind)} из «{plan.SourceName}»: добавлено {outcome.Inserted}, обновлено {outcome.Updated}, пропущено {outcome.Skipped}, с ошибками {outcome.Errors}", ct);
        return outcome;
    }

    // ---- shared ------------------------------------------------------------------------------------------------------

    private sealed class Context(TableData table, ColumnMapping mapping, ImportOptions options)
    {
        public TableData Table { get; } = table;
        public ImportOptions Options { get; } = options;

        public string Cell(IReadOnlyList<string> row, string field)
        {
            var i = mapping[field];
            return i >= 0 && i < row.Count ? row[i].Trim() : "";
        }

        public bool Has(string field) => mapping.IsMapped(field);

        /// <summary>Source row number as the user sees it in Excel: the header is row 1.</summary>
        public static int Number(int index) => index + 2;

        public IEnumerable<(int Number, IReadOnlyList<string> Row)> Rows() =>
            Table.Rows.Select((r, i) => (Number(i), r)).Where(x => x.r.Any(c => !string.IsNullOrWhiteSpace(c)));
    }

    private static PlannedRow Error(int number, string message, string summary) =>
        new(number, ImportAction.Skip, RowSeverity.Error, message, summary);

    /// <summary>A truck weighing a few kilograms usually means a file in tonnes read as kilograms.</summary>
    private static string? SmallMassWarning(params int?[] masses) =>
        masses.Any(m => m is > 0 and < 500)
            ? "Масса меньше 500 кг — возможно, в файле тонны. Выберите единицу «Тонны», если это так."
            : null;

    private static string? Opt(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- vehicles ----------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<PlannedRow>> PlanVehiclesAsync(Context ctx, CancellationToken ct)
    {
        var existing = (await vehicles.GetAllAsync(ct)).ToDictionary(v => v.Plate);
        var seen = new Dictionary<string, int>();
        var result = new List<PlannedRow>();

        foreach (var (number, row) in ctx.Rows())
        {
            var plate = PlateNumber.Normalize(ctx.Cell(row, "plate"));
            var summary = $"{PlateNumber.Format(plate)} · {ctx.Cell(row, "company")}".TrimEnd(' ', '·');
            if (plate.Length == 0)
            {
                result.Add(Error(number, "Не указан госномер.", "(без номера)"));
                continue;
            }

            if (seen.TryGetValue(plate, out var first))
            {
                result.Add(new PlannedRow(number, ImportAction.Skip, RowSeverity.Warning, $"Повтор: этот номер уже встречался в строке {first}.", summary));
                continue;
            }

            seen[plate] = number;

            int? tare = null, max = null;
            string? problem = null;
            if (ctx.Cell(row, "tare") is { Length: > 0 } tareText)
            {
                if (ImportParsing.TryParseMass(tareText, ctx.Options.MassUnit, out var t)) tare = t; else problem = $"Тара «{tareText}» — не число.";
            }

            if (problem is null && ctx.Cell(row, "max") is { Length: > 0 } maxText)
            {
                if (ImportParsing.TryParseMass(maxText, ctx.Options.MassUnit, out var m)) max = m; else problem = $"Допустимая масса «{maxText}» — не число.";
            }

            if (problem is not null)
            {
                result.Add(Error(number, problem, summary));
                continue;
            }

            var incoming = new Vehicle
            {
                Plate = plate,
                Trailer = Opt(PlateNumber.Normalize(ctx.Cell(row, "trailer"))),
                Driver = Opt(ctx.Cell(row, "driver")),
                Company = Opt(ctx.Cell(row, "company")),
                Cargo = Opt(ctx.Cell(row, "cargo")),
                TareKg = tare,
                MaxMassKg = max,
                Note = Opt(ctx.Cell(row, "note")),
            };

            var severity = RowSeverity.Ok;
            var note = "";
            if (tare is { } tr && max is { } mx && tr >= mx)
            {
                severity = RowSeverity.Warning;
                note = "Тара не меньше допустимой массы — проверьте значения.";
            }
            else if (SmallMassWarning(tare, max) is { } small)
            {
                severity = RowSeverity.Warning;
                note = small;
            }

            if (!existing.TryGetValue(plate, out var current))
            {
                result.Add(new PlannedRow(number, ImportAction.Insert, severity, note, summary, incoming));
            }
            else if (!ctx.Options.UpdateExisting)
            {
                result.Add(new PlannedRow(number, ImportAction.Skip, RowSeverity.Ok, "Уже есть в справочнике.", summary));
            }
            else
            {
                var merged = current with
                {
                    Trailer = incoming.Trailer ?? current.Trailer,
                    Driver = incoming.Driver ?? current.Driver,
                    Company = incoming.Company ?? current.Company,
                    Cargo = incoming.Cargo ?? current.Cargo,
                    TareKg = incoming.TareKg ?? current.TareKg,
                    MaxMassKg = incoming.MaxMassKg ?? current.MaxMassKg,
                    Note = incoming.Note ?? current.Note,
                };
                result.Add(merged == current
                    ? new PlannedRow(number, ImportAction.Skip, RowSeverity.Ok, "Без изменений.", summary)
                    : new PlannedRow(number, ImportAction.Update, severity, string.IsNullOrEmpty(note) ? "Будет дополнен." : note, summary, merged));
            }
        }

        return result;
    }

    // ---- name-keyed books (companies, cargo, drivers) -----------------------------------------------------------------

    private static async Task<IReadOnlyList<PlannedRow>> PlanNamesAsync<T>(
        Context ctx, string nameField, IReferenceStore<T> store, Func<T, string> nameOf,
        Func<string, IReadOnlyList<string>, T?, PlannedRow?> specific, CancellationToken ct) where T : class
    {
        var existing = new Dictionary<string, T>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in await store.GetAllAsync(ct))
        {
            existing[nameOf(item)] = item;
        }

        var seen = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var result = new List<PlannedRow>();
        foreach (var (number, row) in ctx.Rows())
        {
            var name = ctx.Cell(row, nameField);
            if (name.Length == 0)
            {
                result.Add(Error(number, "Не указано название.", "(без названия)"));
                continue;
            }

            if (seen.TryGetValue(name, out var first))
            {
                result.Add(new PlannedRow(number, ImportAction.Skip, RowSeverity.Warning, $"Повтор: «{name}» уже встречалось в строке {first}.", name));
                continue;
            }

            seen[name] = number;
            existing.TryGetValue(name, out var current);
            var planned = specific(name, row, current);
            if (planned is not null)
            {
                result.Add(planned with { RowNumber = number });
            }
        }

        return result;
    }

    private static PlannedRow? PlanCompany(Context ctx, string name, IReadOnlyList<string> row, Company? existing)
    {
        var inn = Opt(ctx.Cell(row, "inn"));
        var severity = RowSeverity.Ok;
        var message = "";
        if (inn is not null && !(inn.All(char.IsDigit) && inn.Length is 10 or 12))
        {
            severity = RowSeverity.Warning;
            message = "ИНН должен состоять из 10 или 12 цифр.";
        }

        var incoming = new Company { Name = name, Inn = inn, Note = Opt(ctx.Cell(row, "note")) };
        if (existing is null)
        {
            return new PlannedRow(0, ImportAction.Insert, severity, message, name, incoming);
        }

        if (!ctx.Options.UpdateExisting)
        {
            return new PlannedRow(0, ImportAction.Skip, RowSeverity.Ok, "Уже есть в справочнике.", name);
        }

        var merged = existing with { Inn = incoming.Inn ?? existing.Inn, Note = incoming.Note ?? existing.Note };
        return merged == existing
            ? new PlannedRow(0, ImportAction.Skip, RowSeverity.Ok, "Без изменений.", name)
            : new PlannedRow(0, ImportAction.Update, severity, string.IsNullOrEmpty(message) ? "Будет дополнен." : message, name, merged);
    }

    private static PlannedRow? PlanCargo(string name, IReadOnlyList<string> row, CargoType? existing) =>
        existing is null
            ? new PlannedRow(0, ImportAction.Insert, RowSeverity.Ok, "", name, new CargoType { Name = name })
            : new PlannedRow(0, ImportAction.Skip, RowSeverity.Ok, "Уже есть в справочнике.", name);

    private static PlannedRow? PlanDriver(Context ctx, string name, IReadOnlyList<string> row, Driver? existing)
    {
        var phone = Opt(ctx.Cell(row, "phone"));
        if (existing is null)
        {
            return new PlannedRow(0, ImportAction.Insert, RowSeverity.Ok, "", name, new Driver { FullName = name, Phone = phone });
        }

        if (!ctx.Options.UpdateExisting)
        {
            return new PlannedRow(0, ImportAction.Skip, RowSeverity.Ok, "Уже есть в справочнике.", name);
        }

        var merged = existing with { Phone = phone ?? existing.Phone };
        return merged == existing
            ? new PlannedRow(0, ImportAction.Skip, RowSeverity.Ok, "Без изменений.", name)
            : new PlannedRow(0, ImportAction.Update, RowSeverity.Ok, "Будет дополнен.", name, merged);
    }

    // ---- weighings ---------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<PlannedRow>> PlanWeighingsAsync(Context ctx, CancellationToken ct)
    {
        var parsed = new List<ParsedWeighing>();
        foreach (var (number, row) in ctx.Rows())
        {
            parsed.Add(ParseWeighing(ctx, number, row));
        }

        // Compare with what is already in the journal for the same period, so a file can be imported twice safely.
        var valid = parsed.Where(p => p.Weighing is not null).Select(p => p.Weighing!).ToList();
        var known = new HashSet<string>();
        if (valid.Count > 0)
        {
            var from = valid.Min(w => w.Time).AddDays(-1);
            var to = valid.Max(w => w.Time).AddDays(1);
            foreach (var w in await weighings.QueryAsync(new WeighingQuery { From = from, To = to, Limit = int.MaxValue }, ct))
            {
                known.Add(Fingerprint(w));
            }
        }

        var result = new List<PlannedRow>();
        foreach (var (number, weighing, failure, note) in parsed)
        {
            if (failure is not null)
            {
                result.Add(failure);
                continue;
            }

            var w = weighing!;
            var summary = Summary(w);
            if (!known.Add(Fingerprint(w)))
            {
                result.Add(new PlannedRow(number, ImportAction.Skip, RowSeverity.Warning, "Такая запись уже есть (дата, номер и масса совпадают).", summary));
                continue;
            }

            var warn = note ?? WeighingWarning(w, time.GetUtcNow());
            result.Add(new PlannedRow(number, ImportAction.Insert, warn is null ? RowSeverity.Ok : RowSeverity.Warning, warn ?? "", summary, w));
        }

        return result;
    }

    private sealed record ParsedWeighing(int Number, Weighing? Weighing, PlannedRow? Failure, string? Note = null);

    private ParsedWeighing ParseWeighing(Context ctx, int number, IReadOnlyList<string> row)
    {
        var plate = PlateNumber.Normalize(ctx.Cell(row, "plate"));
        var summary = $"{ctx.Cell(row, "date")} · {PlateNumber.Format(plate)}";
        PlannedRow Fail(string message) => Error(number, message, summary);

        if (plate.Length == 0)
        {
            return new(number, null, Fail("Не указан госномер."));
        }

        if (!ImportParsing.TryParseDateTime(ctx.Cell(row, "date"), ctx.Has("time") ? ctx.Cell(row, "time") : null, out var local))
        {
            return new(number, null, Fail($"Дата «{ctx.Cell(row, "date")}» не распознана (ожидается, например, 28.10.2026 14:32)."));
        }

        int? gross = null, tare = null, net = null;
        foreach (var (field, label) in new[] { ("gross", "Брутто"), ("tare", "Тара"), ("net", "Нетто") })
        {
            var text = ctx.Cell(row, field);
            if (text.Length == 0)
            {
                continue;
            }

            if (!ImportParsing.TryParseMass(text, ctx.Options.MassUnit, out var kg))
            {
                return new(number, null, Fail($"{label} «{text}» — не число (или вне диапазона 1…1 000 000 кг)."));
            }

            switch (field)
            {
                case "gross": gross = kg; break;
                case "tare": tare = kg; break;
                default: net = kg; break;
            }
        }

        var isTare = ImportParsing.ParseKindIsTare(ctx.Cell(row, "kind")) ?? (gross is null && tare is not null);
        string? note = null;

        if (isTare)
        {
            if (tare is null)
            {
                return new(number, null, Fail("Для взвешивания тары не указана масса тары."));
            }

            gross = null;
            net = null;
        }
        else
        {
            if (gross is null)
            {
                return new(number, null, Fail("Для взвешивания брутто не указана масса брутто."));
            }

            if (tare is null && net is not null)
            {
                tare = gross - net; // the file gives gross and net: tare follows
                if (tare <= 0)
                {
                    return new(number, null, Fail($"Нетто {net:N0} кг не меньше брутто {gross:N0} кг."));
                }
            }

            if (tare is not null)
            {
                if (tare >= gross)
                {
                    return new(number, null, Fail($"Тара {tare:N0} кг не меньше брутто {gross:N0} кг."));
                }

                var computed = gross - tare;
                if (net is not null && Math.Abs(net.Value - computed!.Value) > 1)
                {
                    note = $"Нетто в файле ({net:N0}) не совпадает с брутто − тара ({computed:N0}); использовано {computed:N0}.";
                }

                net = computed;
            }
        }

        note ??= SmallMassWarning(gross, tare);

        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var weighing = new Weighing
        {
            Time = new DateTimeOffset(local, offset),
            Plate = plate,
            Trailer = Opt(PlateNumber.Normalize(ctx.Cell(row, "trailer"))),
            Driver = Opt(ctx.Cell(row, "driver")),
            Counterparty = Opt(ctx.Cell(row, "company")),
            Cargo = Opt(ctx.Cell(row, "cargo")),
            Kind = isTare ? WeighingKind.Tare : WeighingKind.Gross,
            GrossKg = gross,
            TareKg = tare,
            NetKg = net,
            Status = WeighingStatus.Completed,
            Operator = Opt(ctx.Cell(row, "operator")),
        };
        return new ParsedWeighing(number, weighing, null, note);
    }

    private static string? WeighingWarning(Weighing w, DateTimeOffset now) =>
        w.Time > now.AddDays(1) ? "Дата в будущем — проверьте формат даты (день и месяц могут быть перепутаны)."
        : w.Time < now.AddYears(-30) ? "Очень старая дата — проверьте формат."
        : null;

    private static string Fingerprint(Weighing w) =>
        string.Create(CultureInfo.InvariantCulture, $"{w.Time.ToUnixTimeSeconds() / 60}|{w.Plate}|{w.Kind}|{w.GrossKg}|{w.TareKg}");

    private static string Summary(Weighing w) =>
        $"{w.Time.ToLocalTime():dd.MM.yyyy HH:mm} · {PlateNumber.Format(w.Plate)} · {DisplayNames.Of(w.Kind)} {DisplayNames.Kg(w.Kind == WeighingKind.Tare ? w.TareKg : w.GrossKg)} кг";
}
