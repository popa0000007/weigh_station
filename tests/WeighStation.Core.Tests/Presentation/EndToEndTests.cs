using System.Threading.Channels;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Infrastructure.Simulation;
using WeighStation.Presentation;
using WeighStation.Presentation.Pages;
using WeighStation.Presentation.Services;

namespace WeighStation.Core.Tests.Presentation;

/// <summary>Runs actions one at a time on a dedicated thread - the same contract as WPF's Dispatcher.</summary>
internal sealed class SerialUi : IUiContext, IDisposable
{
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>();
    private readonly Thread _thread;

    public SerialUi()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.Reader.ReadAllAsync().ToBlockingEnumerable())
            {
                action();
            }
        }) { IsBackground = true, Name = "fake-ui" };
        _thread.Start();
    }

    public void Post(Action action) => _queue.Writer.TryWrite(action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it, like Dispatcher.Invoke.</summary>
    public T Invoke<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>();
        Post(() =>
        {
            try
            {
                done.SetResult(action());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    public void Dispose() => _queue.Writer.Complete();
}

/// <summary>
/// The whole application on the simulator: threads, timers and view models together. Only WPF itself is missing.
/// An operator who presses "capture" as soon as it is enabled should end up with a consistent journal and reports.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weigh-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Operator_works_through_several_trucks_on_the_simulator()
    {
        using var ui = new SerialUi();
        var db = new SqliteDatabase(Path.Combine(_dir, "e2e.db"));
        var journal = new SqliteWeighingRepository(db);
        var vehicles = new SqliteVehicleStore(db);
        var companies = new SqliteCompanyStore(db);
        var cargos = new SqliteCargoStore(db);
        var drivers = new SqliteDriverStore(db);
        await vehicles.SaveAsync(new Vehicle { Plate = "А123ВС197", Company = "Север", Cargo = "Щебень", TareKg = 14320, MaxMassKg = 44000 });

        var hardware = new SimulatedHardware(speed: 25);
        using var session = new WeighingSession(
            new WeighingOptions { StabilityWindow = TimeSpan.FromSeconds(1) }, hardware, hardware, hardware, journal,
            new ReferenceTareProvider(journal, vehicles));
        var settings = new SettingsService(new MemorySettingsStore(), session, "operator");
        var dialogs = new FakeDialogs();

        var weighing = ui.Invoke(() => new WeighingViewModel(
            session, journal, vehicles, companies, cargos, drivers, new ReferenceLearner(vehicles, companies, cargos, drivers), settings, ui));
        var shell = ui.Invoke(() => new ShellViewModel(
            [weighing], session, hardware, settings, TimeProvider.System, ui));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var run = hardware.RunAsync(cts.Token);

        // The operator: whenever the button is enabled, press it.
        var pressed = 0;
        session.Changed += (_, s) =>
        {
            if (s.CanCapture)
            {
                ui.Post(async () =>
                {
                    if (weighing.CaptureCommand.CanExecute(null))
                    {
                        await weighing.CaptureCommand.ExecuteAsync(null);
                        Interlocked.Increment(ref pressed);
                    }
                });
            }
        };

        await TestApp.Eventually(() => Volatile.Read(ref pressed) >= 4 && ui.Invoke(() => weighing.Recent.Count) >= 4, "four weighings", timeoutMs: 60_000);
        hardware.AutoTrucks = false;
        await SettleAsync(session); // let the truck on the platform finish its cycle
        cts.Cancel();
        await run;
        await Task.Delay(100); // let the last learning/refresh finish

        // Journal: every truck is there, in order, with consistent numbers.
        var rows = (await journal.QueryAsync(new WeighingQuery())).Reverse().ToList();
        Assert.True(rows.Count >= 4);
        Assert.Equal(["А123ВС197", "К507МР50", "Н880ОТ77", "Е412КХ197"], rows.Take(4).Select(r => r.Plate));
        Assert.All(rows, r => Assert.Equal(WeighingStatus.Completed, r.Status));
        Assert.Equal(14320, rows[0].TareKg);
        Assert.Equal(rows[0].GrossKg - 14320, rows[0].NetKg);
        Assert.All(rows.Skip(1), r => Assert.Null(r.NetKg)); // tare unknown for the other trucks
        Assert.True(rows.Count == rows.Select(r => r.Id).Distinct().Count());

        // Every weighing has a story.
        foreach (var r in rows)
        {
            var history = await journal.GetHistoryAsync(r.Id);
            Assert.Equal(3, history.Count);
            Assert.StartsWith("Номер распознан", history[0].Text);
        }

        // The reference books learned the new trucks.
        Assert.True((await vehicles.GetAllAsync()).Count >= 4);

        // Reports agree with the journal.
        var reports = new ReportsViewModel(journal, dialogs, TimeProvider.System);
        reports.DateFrom = DateTime.Today.AddDays(-1);
        reports.DateTo = DateTime.Today.AddDays(1);
        await reports.BuildCommand.ExecuteAsync(null);
        Assert.Equal(rows.Count, int.Parse(reports.Totals.Split("Взвешиваний: ")[1].Split(' ')[0]));
        Assert.NotEqual("", reports.Warning);

        // Journal page shows the same rows and can export them.
        var page = new JournalViewModel(journal, dialogs, new FakePrinter(), settings, TimeProvider.System);
        await page.ActivateAsync();
        Assert.Equal(rows.Count, page.Rows.Count);
        dialogs.SavePath = Path.Combine(_dir, "journal.csv");
        page.ExportCommand.Execute(null);
        Assert.Equal(rows.Count + 1, File.ReadAllLines(dialogs.SavePath).Length);

        // Nothing is left half-done on the platform.
        Assert.Equal(TrafficLight.Green, hardware.Light);
        shell.Dispose();
        weighing.Dispose();
    }

    /// <summary>Waits until the platform has been free (Idle) for a while - no truck is about to arrive.</summary>
    private static async Task SettleAsync(WeighingSession session)
    {
        var idleSince = DateTime.UtcNow;
        for (var i = 0; i < 1500; i++)
        {
            if (session.Snapshot.State != SessionState.Idle)
            {
                idleSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - idleSince > TimeSpan.FromMilliseconds(600))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("the platform never became free");
    }

    [Fact]
    public async Task Dead_scale_blocks_the_operator_and_recovery_needs_a_fresh_stable_reading()
    {
        using var ui = new SerialUi();
        var db = new SqliteDatabase(Path.Combine(_dir, "e2e2.db"));
        var journal = new SqliteWeighingRepository(db);
        var vehicles = new SqliteVehicleStore(db);
        var hardware = new SimulatedHardware(speed: 25);
        using var session = new WeighingSession(
            new WeighingOptions { StabilityWindow = TimeSpan.FromSeconds(1), ScaleTimeout = TimeSpan.FromSeconds(1) },
            hardware, hardware, hardware, journal, new ReferenceTareProvider(journal, vehicles));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = hardware.RunAsync(cts.Token);

        await TestApp.Eventually(() => session.Snapshot.CanCapture, "truck ready", timeoutMs: 30_000);
        hardware.ScaleConnected = false;

        // Without Tick() the session cannot notice silence - this is what the shell's 1 s timer is for.
        for (var i = 0; i < 100 && session.Snapshot.CanCapture; i++)
        {
            session.Tick();
            await Task.Delay(50);
        }

        Assert.False(session.Snapshot.CanCapture);
        Assert.Equal("Нет данных от весов", session.Snapshot.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureAsync());

        hardware.ScaleConnected = true;
        await TestApp.Eventually(() => session.Snapshot.CanCapture, "recovery", timeoutMs: 30_000);
        await session.CaptureAsync();
        Assert.Single(await journal.GetRecentAsync(10));

        hardware.AutoTrucks = false;
        cts.Cancel();
        await run;
    }
}
