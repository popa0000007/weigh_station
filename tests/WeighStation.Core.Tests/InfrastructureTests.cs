using WeighStation.Core.Models;
using WeighStation.Infrastructure.Persistence;
using WeighStation.Infrastructure.Simulation;

namespace WeighStation.Core.Tests;

public class InfrastructureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weigh-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_roundtrips_a_weighing_including_nulls()
    {
        var repo = new SqliteWeighingRepository(Path.Combine(_dir, "w.db"));
        var saved = await repo.AddAsync(new Weighing
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

        var recent = await repo.GetRecentAsync(10);

        var row = Assert.Single(recent);
        Assert.Equal(saved.Id, row.Id);
        Assert.Equal("ООО «Северный карьер»", row.Counterparty);
        Assert.Null(row.Driver);
        Assert.Equal(28460, row.NetKg);
        Assert.Equal(1_700_000_000, row.Time.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Recent_returns_newest_first_and_respects_limit()
    {
        var repo = new SqliteWeighingRepository(Path.Combine(_dir, "w.db"));
        for (var i = 1; i <= 5; i++)
        {
            await repo.AddAsync(new Weighing { Time = DateTimeOffset.Now, Plate = $"P{i}", GrossKg = i });
        }

        var recent = await repo.GetRecentAsync(3);

        Assert.Equal(["P5", "P4", "P3"], recent.Select(w => w.Plate));
    }

    [Fact]
    public async Task Tare_lookup_uses_latest_tare_weighing_of_that_plate_only()
    {
        var repo = new SqliteWeighingRepository(Path.Combine(_dir, "w.db"));
        await repo.AddAsync(new Weighing { Time = DateTimeOffset.Now, Plate = "A", Kind = WeighingKind.Tare, TareKg = 14000 });
        await repo.AddAsync(new Weighing { Time = DateTimeOffset.Now, Plate = "A", Kind = WeighingKind.Tare, TareKg = 14320 });
        await repo.AddAsync(new Weighing { Time = DateTimeOffset.Now, Plate = "A", Kind = WeighingKind.Gross, GrossKg = 40000 });
        await repo.AddAsync(new Weighing { Time = DateTimeOffset.Now, Plate = "B", Kind = WeighingKind.Tare, TareKg = 9000 });

        Assert.Equal(14320, await repo.FindTareKgAsync("A"));
        Assert.Equal(9000, await repo.FindTareKgAsync("B"));
        Assert.Null(await repo.FindTareKgAsync("C"));
    }

    [Fact]
    public async Task Session_with_simulated_hardware_completes_full_cycles()
    {
        var repo = new SqliteWeighingRepository(Path.Combine(_dir, "w.db"));
        var hw = new SimulatedHardware(speed: 25);
        using var session = new WeighingSession(
            new WeighingOptions { StabilityWindow = TimeSpan.FromSeconds(1) }, hw, hw, hw, repo, repo);
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
        Assert.Equal(result.Count, (await repo.GetRecentAsync(100)).Count);
    }
}
