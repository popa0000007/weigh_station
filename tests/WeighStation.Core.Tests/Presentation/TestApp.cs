using WeighStation.Core;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Import;
using WeighStation.Core.Models;
using WeighStation.Core.Security;
using WeighStation.Infrastructure.Import;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Infrastructure.Simulation;
using WeighStation.Presentation;
using WeighStation.Presentation.Pages;
using WeighStation.Presentation.Services;

namespace WeighStation.Core.Tests.Presentation;

internal sealed class ImmediateUi : IUiContext
{
    public void Post(Action action) => action();
}

internal sealed class TestTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 14, 30, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Questions { get; } = [];
    public string? SavePath { get; set; }
    public List<string> SaveSuggestions { get; } = [];
    public List<string> OpenedFolders { get; } = [];
    public string? OpenPath { get; set; }
    public List<string> OpenFilters { get; } = [];
    public int PasswordDialogs { get; private set; }

    public bool Confirm(string title, string message)
    {
        Questions.Add(message);
        return ConfirmAnswer;
    }

    public string? PickSaveFile(string title, string suggestedFileName, string filter)
    {
        SaveSuggestions.Add(suggestedFileName);
        return SavePath;
    }

    public void OpenFolder(string path) => OpenedFolders.Add(path);

    public string? PickOpenFile(string title, string filter)
    {
        OpenFilters.Add(filter);
        return OpenPath;
    }

    public void ShowChangePassword() => PasswordDialogs++;
}

internal sealed class FakePrinter : ITicketPrinter
{
    public List<(Weighing Weighing, IReadOnlyList<AuditEntry> History)> Printed { get; } = [];
    public Exception? FailWith { get; set; }

    public void Print(Weighing weighing, IReadOnlyList<AuditEntry> history)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Printed.Add((weighing, history));
    }
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    public AppSettings Saved { get; private set; } = new();
    public Exception? FailWith { get; set; }

    public AppSettings Load() => Saved;

    public void Save(AppSettings settings)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Saved = settings;
    }
}

/// <summary>Lets a test make the next save fail, to check that the screen reports it instead of crashing.</summary>
internal sealed class FlakyRepository(IWeighingRepository inner) : IWeighingRepository
{
    public Exception? FailAdd { get; set; }

    public Task<Weighing> AddAsync(Weighing weighing, IReadOnlyList<AuditEntry> history, CancellationToken ct = default) =>
        FailAdd is null ? inner.AddAsync(weighing, history, ct) : Task.FromException<Weighing>(FailAdd);

    public Task<IReadOnlyList<Weighing>> GetRecentAsync(int count, CancellationToken ct = default) => inner.GetRecentAsync(count, ct);

    public Task<IReadOnlyList<Weighing>> QueryAsync(WeighingQuery query, CancellationToken ct = default) => inner.QueryAsync(query, ct);

    public Task<Weighing?> GetAsync(long id, CancellationToken ct = default) => inner.GetAsync(id, ct);

    public Task UpdateDescriptionAsync(Weighing edited, AuditEntry entry, CancellationToken ct = default) =>
        inner.UpdateDescriptionAsync(edited, entry, ct);

    public Task CancelAsync(long id, AuditEntry entry, CancellationToken ct = default) => inner.CancelAsync(id, entry, ct);

    public Task<IReadOnlyList<AuditEntry>> GetHistoryAsync(long id, CancellationToken ct = default) => inner.GetHistoryAsync(id, ct);

    public Task<int> ImportAsync(IReadOnlyList<(Weighing Weighing, AuditEntry Entry)> items, IProgress<int>? progress = null, CancellationToken ct = default) =>
        inner.ImportAsync(items, progress, ct);
}

/// <summary>The whole application wired like in production, except hardware, dialogs and clock.</summary>
internal sealed class TestApp : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "weigh-ui-" + Guid.NewGuid().ToString("N"));
    public SqliteDatabase Db { get; }
    public SqliteWeighingRepository RealRepo { get; }
    public FlakyRepository Repo { get; }
    public SqliteVehicleStore Vehicles { get; }
    public SqliteCompanyStore Companies { get; }
    public SqliteCargoStore Cargos { get; }
    public SqliteDriverStore Drivers { get; }
    public FakeDevices Dev { get; } = new();
    public SimulatedHardware Sim { get; } = new();
    public TestTime Time { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakePrinter Printer { get; } = new();
    public MemorySettingsStore SettingsStore { get; } = new();
    public WeighingSession Session { get; }
    public SettingsService Settings { get; }
    public CurrentUser User { get; } = new();
    public SqliteUserStore UserStore { get; }
    public SqliteAuditLog Audit { get; }
    public UserService Users { get; }
    public ReferenceLearner Learner { get; }
    public ImmediateUi Ui { get; } = new();

    public TestApp()
    {
        Db = new SqliteDatabase(Path.Combine(Dir, "app.db"));
        RealRepo = new SqliteWeighingRepository(Db);
        Repo = new FlakyRepository(RealRepo);
        Vehicles = new SqliteVehicleStore(Db);
        Companies = new SqliteCompanyStore(Db);
        Cargos = new SqliteCargoStore(Db);
        Drivers = new SqliteDriverStore(Db);
        var tare = new ReferenceTareProvider(RealRepo, Vehicles);
        Session = new WeighingSession(new WeighingOptions(), Dev, Dev, Dev, Repo, tare, () => Time.Now);
        Settings = new SettingsService(SettingsStore, Session);
        UserStore = new SqliteUserStore(Db);
        Audit = new SqliteAuditLog(Db, Time);
        Users = new UserService(UserStore, new Pbkdf2PasswordHasher(1000), Audit, Time);
        SignInAs(UserRole.Administrator);
        Learner = new ReferenceLearner(Vehicles, Companies, Cargos, Drivers);
    }

    public WeighingViewModel NewWeighing() =>
        new(Session, Repo, Vehicles, Companies, Cargos, Drivers, Learner, User, Ui);

    public JournalViewModel NewJournal() => new(Repo, Dialogs, Printer, User, Time);

    public ReferencesViewModel NewReferences() => new(Vehicles, Companies, Cargos, Drivers, Dialogs, User);

    public ReportsViewModel NewReports() => new(Repo, Dialogs, User, Time);

    public DevicesViewModel NewDevices(bool simulation = true) =>
        new(Session, Sim, simulation ? Sim : null, Settings, User, Dialogs, Ui);

    public ImportService Importer => new(Vehicles, Companies, Cargos, Drivers, RealRepo, Audit, Time);

    public ImportViewModel NewImport() => new(Importer, new FileTableReader(), Dialogs, User, Time);

    /// <summary>Writes a UTF-8 CSV into the test directory and returns its path.</summary>
    public string WriteCsv(string name, string content)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(true));
        return path;
    }

    public SettingsViewModel NewSettings() => new(Settings, Db, Dialogs, User, Time);

    /// <summary>Signs in a user with the given role (not stored in the database).</summary>
    public User SignInAs(UserRole role, string name = "Тестов А.А.")
    {
        var user = new User { Id = 1000 + (int)role, Login = "tester", FullName = name, Role = role, CreatedAt = Time.Now };
        User.SignIn(user);
        return user;
    }

    /// <summary>A truck stands on the platform, the camera has read its plate, the weight has settled.</summary>
    public void SettledTruck(int kg = 42780, string plate = "А123ВС197", double from = 0, double to = 3)
    {
        Dev.Sensors(onPlatform: true);
        Dev.Plate(plate);
        Dev.Hold(kg, from, to);
    }

    public Task<Weighing> AddWeighing(
        string plate = "А123ВС197", string? company = "Север", string? cargo = "Щебень", int? net = 28000,
        WeighingStatus status = WeighingStatus.Completed, DateTimeOffset? time = null, WeighingKind kind = WeighingKind.Gross) =>
        RealRepo.AddAsync(
            new Weighing
            {
                Time = time ?? Time.Now,
                Plate = plate,
                Counterparty = company,
                Cargo = cargo,
                Kind = kind,
                GrossKg = net + 14000,
                TareKg = 14000,
                NetKg = net,
                Status = status,
                Operator = "ivan",
            },
            [new AuditEntry(time ?? Time.Now, "ivan", "Вес зафиксирован оператором")]);

    public void Dispose()
    {
        Session.Dispose();
        if (Directory.Exists(Dir))
        {
            Directory.Delete(Dir, recursive: true);
        }
    }

    /// <summary>Waits for fire-and-forget work (reference lookup, learning) started by view-model handlers.</summary>
    public static async Task Eventually(Func<bool> condition, string because = "condition", int timeoutMs = 2000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 10)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }
}
