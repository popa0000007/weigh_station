using System.IO;
using System.Windows;
using System.Windows.Threading;
using WeighStation.App.Services;
using WeighStation.Core;
using WeighStation.Core.Models;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Infrastructure.Simulation;
using WeighStation.Presentation;
using WeighStation.Presentation.Pages;

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

        // ---- Composition root: the only place that knows which concrete classes are used. ----
        var db = new SqliteDatabase(Path.Combine(DataDir, "weighings.db"));
        var journal = new SqliteWeighingRepository(db);
        var vehicles = new SqliteVehicleStore(db);
        var companies = new SqliteCompanyStore(db);
        var cargos = new SqliteCargoStore(db);
        var drivers = new SqliteDriverStore(db);

        // To work with real equipment replace this with implementations of IScale, IPlateRecognizer, IPlatformIo
        // and IDeviceMonitor (see README). ISimulationControl is then null and the simulation panel disappears.
        var hardware = new SimulatedHardware();

        _session = new WeighingSession(
            new WeighingOptions(), hardware, hardware, hardware, journal, new ReferenceTareProvider(journal, vehicles));
        var settings = new SettingsService(new JsonSettingsStore(Path.Combine(DataDir, "settings.json")), _session, Environment.UserName);

        var ui = new WpfUiContext(Dispatcher);
        var dialogs = new WpfDialogService();
        var learner = new ReferenceLearner(vehicles, companies, cargos, drivers);

        PageViewModel[] pages =
        [
            new WeighingViewModel(_session, journal, vehicles, companies, cargos, drivers, learner, settings, ui),
            new JournalViewModel(journal, dialogs, new TicketPrinter(), settings, TimeProvider.System),
            new ReferencesViewModel(vehicles, companies, cargos, drivers, dialogs),
            new ReportsViewModel(journal, dialogs, TimeProvider.System),
            new DevicesViewModel(_session, hardware, hardware, settings, dialogs, ui),
            new SettingsViewModel(settings, db, dialogs, TimeProvider.System),
        ];
        _shell = new ShellViewModel(pages, _session, hardware, settings, TimeProvider.System, ui);

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();
        _ = _shell.StartAsync();

        // Once a second: clock and the scale watchdog.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _shell.Tick();
        _timer.Start();

        _hardware = hardware.RunAsync(_shutdown.Token);
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
