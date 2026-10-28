using System.IO;
using System.Windows;
using System.Windows.Threading;
using WeighStation.App.Services;
using WeighStation.Core;
using WeighStation.Core.Import;
using WeighStation.Core.Security;
using WeighStation.Infrastructure.Import;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Infrastructure.Simulation;
using WeighStation.Presentation;
using WeighStation.Presentation.Pages;
using WeighStation.Presentation.Security;

namespace WeighStation.App;

public partial class App : Application
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeighStation");

    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _singleInstance;
    private WeighingSession? _session;
    private ShellViewModel? _shell;
    private DispatcherTimer? _timer;
    private UserService? _users;
    private CurrentUser? _currentUser;
    private Task _hardware = Task.CompletedTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Two instances would fight over the same barrier and database, so refuse the second one.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\WeighStation.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("АвтоВесы уже запущены.", "АвтоВесы", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // The application ends when we say so (login cancelled, main window closed), not when a window closes.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- Composition root: the only place that knows which concrete classes are used. ----
        var db = new SqliteDatabase(Path.Combine(DataDir, "weighings.db"));
        var journal = new SqliteWeighingRepository(db);
        var vehicles = new SqliteVehicleStore(db);
        var companies = new SqliteCompanyStore(db);
        var cargos = new SqliteCargoStore(db);
        var drivers = new SqliteDriverStore(db);
        var audit = new SqliteAuditLog(db, TimeProvider.System);
        _users = new UserService(new SqliteUserStore(db), new Pbkdf2PasswordHasher(), audit, TimeProvider.System);
        _currentUser = new CurrentUser();

        // To work with real equipment replace this with implementations of IScale, IPlateRecognizer, IPlatformIo
        // and IDeviceMonitor (see README). ISimulationControl is then null and the simulation panel disappears.
        var hardware = new SimulatedHardware();

        _session = new WeighingSession(
            new WeighingOptions(), hardware, hardware, hardware, journal, new ReferenceTareProvider(journal, vehicles));
        var settings = new SettingsService(new JsonSettingsStore(Path.Combine(DataDir, "settings.json")), _session);

        var ui = new WpfUiContext(Dispatcher);
        var dialogs = new WpfDialogService(_users, _currentUser);
        var learner = new ReferenceLearner(vehicles, companies, cargos, drivers);

        PageViewModel[] pages =
        [
            new WeighingViewModel(_session, journal, vehicles, companies, cargos, drivers, learner, _currentUser, ui),
            new JournalViewModel(journal, dialogs, new TicketPrinter(), _currentUser, TimeProvider.System),
            new ReferencesViewModel(vehicles, companies, cargos, drivers, dialogs, _currentUser),
            new ReportsViewModel(journal, dialogs, _currentUser, TimeProvider.System),
            new DevicesViewModel(_session, hardware, hardware, settings, _currentUser, dialogs, ui),
            new ImportViewModel(
                new ImportService(vehicles, companies, cargos, drivers, journal, audit, TimeProvider.System),
                new FileTableReader(), dialogs, _currentUser, TimeProvider.System),
            new UsersViewModel(_users, audit, _currentUser, dialogs),
            new SettingsViewModel(settings, db, dialogs, _currentUser, TimeProvider.System),
        ];
        _shell = new ShellViewModel(pages, _session, hardware, _currentUser, _users, dialogs, TimeProvider.System, ui);

        // Hardware runs from the start: trucks may arrive while the login window is open.
        _hardware = hardware.RunAsync(_shutdown.Token);

        if (!ShowLogin())
        {
            Shutdown();
            return;
        }

        var window = new MainWindow { DataContext = _shell };
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        _shell.LoggedOut += (_, _) => OnLoggedOut(window);
        window.Show();
        _ = _shell.StartAsync();

        // Once a second: clock and the scale watchdog.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _shell.Tick();
        _timer.Start();
    }

    private bool ShowLogin() =>
        new LoginWindow(new LoginViewModel(_users!, _currentUser!)).ShowDialog() == true;

    /// <summary>After "Выйти": hide the main window, ask for credentials again, bring the window back for whoever signs in.</summary>
    private void OnLoggedOut(Window main)
    {
        main.Hide();
        if (ShowLogin())
        {
            main.Show();
        }
        else
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _shutdown.Cancel();
        try
        {
            _hardware.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _shell?.Dispose();
        _session?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch (IOException)
        {
            // Nothing sensible left to do if the log itself can't be written.
        }

        MessageBox.Show(
            $"Произошла непредвиденная ошибка:\n{e.Exception.Message}\n\nПодробности записаны в журнал ошибок.",
            "АвтоВесы", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
