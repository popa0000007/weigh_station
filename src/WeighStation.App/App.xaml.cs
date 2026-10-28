using System.IO;
using System.Windows;
using WeighStation.App.ViewModels;
using WeighStation.Core;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Infrastructure.Simulation;

namespace WeighStation.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private WeighingSession? _session;
    private Task _hardware = Task.CompletedTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Composition root. To work with real equipment replace SimulatedHardware with
        // implementations of IScale, IPlateRecognizer and IPlatformIo (see README).
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeighStation", "weighings.db");
        var repository = new SqliteWeighingRepository(dbPath);
        var hardware = new SimulatedHardware();
        _session = new WeighingSession(new WeighingOptions(), hardware, hardware, hardware, repository, repository);

        var viewModel = new MainViewModel(_session, repository, Environment.UserName);
        new MainWindow { DataContext = viewModel }.Show();

        _hardware = hardware.RunAsync(_shutdown.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        try
        {
            _hardware.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _session?.Dispose();
        base.OnExit(e);
    }
}
