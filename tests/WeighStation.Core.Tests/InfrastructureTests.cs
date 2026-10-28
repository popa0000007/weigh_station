using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Infrastructure.Simulation;

namespace WeighStation.Core.Tests;

public class InfrastructureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weigh-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _db;
    private readonly SqliteWeighingRepository _repo;

    public InfrastructureTests()
    {
        _db = new SqliteDatabase(Path.Combine(_dir, "w.db"));
        _repo = new SqliteWeighingRepository(_db);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly AuditEntry[] NoHistory = [];

    private Task<Weighing> Add(Weighing w) => _repo.AddAsync(w, NoHistory);

    private static Weighing W(string plate, DateTimeOffset? time = null) =>
        new() { Time = time ?? DateTimeOffset.Now, Plate = plate, GrossKg = 1000 };

    [Fact]
    public async Task Repository_roundtrips_a_weighing_including_nulls()
    {
        var saved = await Add(new Weighing
        {
            Time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            Plate = "А123ВС197",
            Counterparty = "ООО «Северный карьер»",
            Kind = WeighingKind.Gross,
            GrossKg = 42780,
            TareKg = 14320,
            NetKg = 28460,
            Status = WeighingStatus.Completed,
        });

        var row = Assert.Single(await _repo.GetRecentAsync(10));
        Assert.Equal(saved.Id, row.Id);
        Assert.Equal("ООО «Северный карьер»", row.Counterparty);
        Assert.Null(row.Driver);
        Assert.Equal(28460, row.NetKg);
        Assert.Equal(1_700_000_000, row.Time.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Plate_is_stored_in_canonical_form()
    {
        await Add(W("a 123 bc-197"));
        Assert.Equal("А123ВС197", (await _repo.GetRecentAsync(1)).Single().Plate);
    }

    [Fact]
    public async Task Recent_returns_newest_first_and_respects_limit()
    {
        var t = DateTimeOffset.Now;
        for (var i = 1; i <= 5; i++)
        {
            await Add(W($"Т{i}", t.AddMinutes(i)));
        }

        Assert.Equal(["Т5", "Т4", "Т3"], (await _repo.GetRecentAsync(3)).Select(w => w.Plate));
    }

    [Fact]
    public async Task History_is_stored_with_the_weighing_in_chronological_order()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var w = await _repo.AddAsync(W("A"), [
            new AuditEntry(t.AddSeconds(5), "ivan", "второе"),
            new AuditEntry(t, "ivan", "первое"),
        ]);

        var history = await _repo.GetHistoryAsync(w.Id);

        Assert.Equal(["первое", "второе"], history.Select(h => h.Text));
        Assert.All(history, h => Assert.Equal("ivan", h.User));
    }

    [Fact]
    public async Task Query_filters_by_period_plate_status_and_company()
    {
        var day = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        await Add(W("А111АА77", day) with { Counterparty = "ООО «Север»" });
        await Add(W("В222ВВ77", day.AddDays(1)) with { Counterparty = "ИП Смирнов" });
        var cancelled = await Add(W("А333АА50", day.AddDays(2)) with { Counterparty = "ООО «Север»" });
        await _repo.CancelAsync(cancelled.Id, new AuditEntry(day, "u", "отмена"));

        Assert.Equal(3, (await _repo.QueryAsync(new WeighingQuery())).Count);
        Assert.Equal(2, (await _repo.QueryAsync(new WeighingQuery { From = day, To = day.AddDays(2) })).Count);
        Assert.Equal(["А333АА50", "А111АА77"],
            (await _repo.QueryAsync(new WeighingQuery { Plate = "a" })).Select(w => w.Plate));
        Assert.Single(await _repo.QueryAsync(new WeighingQuery { Plate = "в222" }));
        Assert.Single(await _repo.QueryAsync(new WeighingQuery { Status = WeighingStatus.Cancelled }));
        Assert.Equal(2, (await _repo.QueryAsync(new WeighingQuery { Company = "СЕВЕР" })).Count);
        Assert.Single(await _repo.QueryAsync(new WeighingQuery { Company = "север", Limit = 1 }));
    }

    [Fact]
    public async Task Edit_changes_description_marks_manual_edit_and_logs_history()
    {
        var w = await Add(W("А111АА77") with { Counterparty = "Старый", GrossKg = 30000 });

        await _repo.UpdateDescriptionAsync(
            w with { Counterparty = "Новый", Driver = "Петров" },
            new AuditEntry(DateTimeOffset.Now, "ivan", "Контрагент: «Старый» → «Новый»"));

        var after = (await _repo.GetAsync(w.Id))!;
        Assert.Equal("Новый", after.Counterparty);
        Assert.Equal("Петров", after.Driver);
        Assert.Equal(30000, after.GrossKg);
        Assert.Equal(WeighingStatus.ManualEdit, after.Status);
        Assert.Equal("Контрагент: «Старый» → «Новый»", Assert.Single(await _repo.GetHistoryAsync(w.Id)).Text);
    }

    [Fact]
    public async Task Edit_of_a_cancelled_weighing_keeps_it_cancelled()
    {
        var w = await Add(W("А111АА77"));
        await _repo.CancelAsync(w.Id, new AuditEntry(DateTimeOffset.Now, "u", "отмена"));
        await _repo.UpdateDescriptionAsync(w with { Driver = "X" }, new AuditEntry(DateTimeOffset.Now, "u", "правка"));
        Assert.Equal(WeighingStatus.Cancelled, (await _repo.GetAsync(w.Id))!.Status);
    }

    [Fact]
    public async Task Updating_a_missing_weighing_fails()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repo.UpdateDescriptionAsync(W("A") with { Id = 99 }, new AuditEntry(DateTimeOffset.Now, "u", "x")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repo.CancelAsync(99, new AuditEntry(DateTimeOffset.Now, "u", "x")));
    }

    [Fact]
    public async Task Tare_lookup_uses_latest_tare_weighing_of_that_plate_and_ignores_cancelled()
    {
        await Add(W("A") with { Kind = WeighingKind.Tare, TareKg = 14000 });
        var latest = await Add(W("A") with { Kind = WeighingKind.Tare, TareKg = 14320 });
        await Add(W("A") with { Kind = WeighingKind.Gross, GrossKg = 40000 });
        await Add(W("B") with { Kind = WeighingKind.Tare, TareKg = 9000 });

        Assert.Equal(14320, await _repo.FindTareKgAsync("A"));
        Assert.Equal(9000, await _repo.FindTareKgAsync("B"));
        Assert.Null(await _repo.FindTareKgAsync("C"));

        await _repo.CancelAsync(latest.Id, new AuditEntry(DateTimeOffset.Now, "u", "отмена"));
        Assert.Equal(14000, await _repo.FindTareKgAsync("A"));
    }

    [Fact]
    public async Task Tare_provider_falls_back_to_the_reference_book()
    {
        var vehicles = new SqliteVehicleStore(_db);
        await vehicles.SaveAsync(new Vehicle { Plate = "А111АА77", TareKg = 12000 });
        var provider = new ReferenceTareProvider(_repo, vehicles);

        Assert.Equal(12000, await provider.FindTareKgAsync("a111aa 77"));

        await Add(W("А111АА77") with { Kind = WeighingKind.Tare, TareKg = 12500 });
        Assert.Equal(12500, await provider.FindTareKgAsync("А111АА77"));
        Assert.Null(await provider.FindTareKgAsync("Х999ХХ99"));
    }

    [Fact]
    public async Task Vehicle_store_normalises_plates_and_finds_by_any_spelling()
    {
        var store = new SqliteVehicleStore(_db);
        var id = await store.SaveAsync(new Vehicle
        {
            Plate = "a 123 bc 197", Trailer = "ab 4567 77", Driver = " Петров ", MaxMassKg = 44000,
        });

        var found = (await store.FindByPlateAsync("А123ВС197"))!;

        Assert.Equal(id, found.Id);
        Assert.Equal("А123ВС197", found.Plate);
        Assert.Equal("АВ456777", found.Trailer);
        Assert.Equal("Петров", found.Driver);
        Assert.Equal(44000, found.MaxMassKg);
        Assert.Null(await store.FindByPlateAsync("Х999ХХ99"));
        Assert.Null(await store.FindByPlateAsync(""));
    }

    [Fact]
    public async Task Vehicle_store_updates_in_place_and_deletes()
    {
        var store = new SqliteVehicleStore(_db);
        var id = await store.SaveAsync(new Vehicle { Plate = "А111АА77" });

        var returned = await store.SaveAsync(new Vehicle { Id = id, Plate = "А111АА77", Note = "новая заметка" });

        Assert.Equal(id, returned);
        var only = Assert.Single(await store.GetAllAsync());
        Assert.Equal("новая заметка", only.Note);

        await store.DeleteAsync(id);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Vehicle_store_rejects_duplicate_plates_in_any_spelling()
    {
        var store = new SqliteVehicleStore(_db);
        await store.SaveAsync(new Vehicle { Plate = "А111АА77" });
        await Assert.ThrowsAsync<DuplicateException>(() => store.SaveAsync(new Vehicle { Plate = "a111aa 77" }));
    }

    [Fact]
    public async Task Editing_a_vehicle_is_not_a_duplicate_of_itself()
    {
        var store = new SqliteVehicleStore(_db);
        var id = await store.SaveAsync(new Vehicle { Plate = "А111АА77" });
        await store.SaveAsync(new Vehicle { Id = id, Plate = "А111АА77", Driver = "X" });
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task Name_based_stores_reject_case_insensitive_duplicates_and_sort_alphabetically()
    {
        var companies = new SqliteCompanyStore(_db);
        await companies.SaveAsync(new Company { Name = "ООО «Ярмарка»", Inn = "7700000000" });
        await companies.SaveAsync(new Company { Name = "АО «Север»" });
        await Assert.ThrowsAsync<DuplicateException>(() => companies.SaveAsync(new Company { Name = "ао «север» " }));

        Assert.Equal(["АО «Север»", "ООО «Ярмарка»"], (await companies.GetAllAsync()).Select(c => c.Name));

        var cargos = new SqliteCargoStore(_db);
        await cargos.SaveAsync(new CargoType { Name = "Щебень" });
        await Assert.ThrowsAsync<DuplicateException>(() => cargos.SaveAsync(new CargoType { Name = "щебень" }));

        var drivers = new SqliteDriverStore(_db);
        await drivers.SaveAsync(new Driver { FullName = "Петров С.И.", Phone = "+7 900 000-00-00" });
        Assert.Equal("+7 900 000-00-00", (await drivers.GetAllAsync()).Single().Phone);
    }

    [Fact]
    public void Settings_roundtrip_through_json_and_survive_a_damaged_file()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonSettingsStore(path);
        Assert.Equal(new AppSettings().Weighing, store.Load().Weighing);

        var settings = new AppSettings
        {
            OperatorName = "Иванов",
            Weighing = new WeighingOptions { MinWeightKg = 700, AutoCapture = true, StabilityWindow = TimeSpan.FromSeconds(3) },
            Devices = new DeviceSettings { PlcHost = "10.0.0.5" },
        };
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal(settings, loaded);

        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void Simulator_reports_device_status_and_can_break_things()
    {
        var hw = new SimulatedHardware();
        Assert.All(hw.GetStatuses(), s => Assert.Equal(DeviceHealth.Online, s.Health));

        var notified = 0;
        hw.StatusChanged += (_, _) => notified++;
        hw.ScaleConnected = false;
        hw.CamerasConnected = false;
        hw.CamerasConnected = false;

        Assert.Equal(2, notified);
        var offline = hw.GetStatuses().Where(s => s.Health == DeviceHealth.Offline).Select(s => s.Name);
        Assert.Equal(["Терминал весов", "Камера 1", "Камера 2"], offline);
    }

    [Fact]
    public async Task Session_with_simulated_hardware_completes_full_cycles()
    {
        var hw = new SimulatedHardware(speed: 25);
        using var session = new WeighingSession(
            new WeighingOptions { StabilityWindow = TimeSpan.FromSeconds(1) }, hw, hw, hw, _repo, _repo);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var captured = new List<Weighing>();
        var autoCapture = Task.CompletedTask;
        session.Changed += (_, s) =>
        {
            if (s.CanCapture)
            {
                autoCapture = session.CaptureAsync(cts.Token).ContinueWith(
                    t => { if (t.IsCompletedSuccessfully) { lock (captured) { captured.Add(t.Result); } } },
                    TaskScheduler.Default);
            }
        };

        var run = hw.RunAsync(cts.Token);
        while (!cts.IsCancellationRequested)
        {
            lock (captured)
            {
                if (captured.Count >= 3)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        cts.Cancel();
        await run;
        await autoCapture;

        List<Weighing> result;
        lock (captured)
        {
            result = [.. captured];
        }

        Assert.True(result.Count >= 3, $"only {result.Count} weighings captured");
        Assert.Equal(["А123ВС197", "К507МР50", "Н880ОТ77"], result.Take(3).Select(w => w.Plate));
        Assert.All(result, w => Assert.InRange(w.GrossKg!.Value, 38000, 45000));
        Assert.Equal(result.Count, (await _repo.GetRecentAsync(100)).Count);
        Assert.NotEmpty(await _repo.GetHistoryAsync(result[0].Id));
    }
}
