using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Core.Tests;

internal sealed class FakeDevices : IScale, IPlateRecognizer, IPlatformIo
{
    private PlatformState _state;

    public event EventHandler<WeightSample>? SampleReceived;
    public event EventHandler<PlateReading>? PlateRecognized;
    public event EventHandler<PlatformState>? StateChanged;

    public PlatformState Current => _state;
    public TrafficLight Light { get; private set; } = TrafficLight.Green;
    public BarrierPosition Barrier { get; private set; } = BarrierPosition.Closed;

    public void SetLight(TrafficLight light) => Light = light;
    public void SetBarrier(BarrierPosition position) => Barrier = position;

    public void Sensors(bool entry = false, bool onPlatform = false, bool exit = false)
    {
        _state = new PlatformState(entry, onPlatform, exit);
        StateChanged?.Invoke(this, _state);
    }

    public void Plate(string plate, double confidence = 0.95, DateTimeOffset? at = null) =>
        PlateRecognized?.Invoke(this, new PlateReading(plate, confidence, "cam1", at ?? DateTimeOffset.UnixEpoch));

    public void Sample(int kg, double atSeconds, bool stable = true) =>
        SampleReceived?.Invoke(this, new WeightSample(kg, stable, DateTimeOffset.UnixEpoch.AddSeconds(atSeconds)));

    /// <summary>Feeds constant weight once per 0.5 s from <paramref name="from"/> to <paramref name="to"/> seconds.</summary>
    public void Hold(int kg, double from, double to, bool stable = true)
    {
        for (var t = from; t <= to; t += 0.5)
        {
            Sample(kg, t, stable);
        }
    }
}

internal sealed class FakeRepository : IWeighingRepository, ITareProvider
{
    public List<Weighing> Stored { get; } = [];
    public Dictionary<long, List<AuditEntry>> History { get; } = [];
    public Dictionary<string, int> Tares { get; } = [];
    public Exception? FailWith { get; set; }

    public Task<Weighing> AddAsync(Weighing weighing, IReadOnlyList<AuditEntry> history, CancellationToken ct = default)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        var stored = weighing with { Id = Stored.Count + 1 };
        Stored.Add(stored);
        History[stored.Id] = [.. history];
        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<Weighing>> GetRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Weighing>>(Stored.AsEnumerable().Reverse().Take(count).ToList());

    public Task<IReadOnlyList<Weighing>> QueryAsync(WeighingQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Weighing>>(Stored.AsEnumerable().Reverse().Take(query.Limit).ToList());

    public Task<Weighing?> GetAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Stored.FirstOrDefault(w => w.Id == id));

    public Task UpdateDescriptionAsync(Weighing edited, AuditEntry entry, CancellationToken ct = default)
    {
        Stored[Stored.FindIndex(w => w.Id == edited.Id)] = edited with { Status = WeighingStatus.ManualEdit };
        History[edited.Id].Add(entry);
        return Task.CompletedTask;
    }

    public Task CancelAsync(long id, AuditEntry entry, CancellationToken ct = default)
    {
        var i = Stored.FindIndex(w => w.Id == id);
        Stored[i] = Stored[i] with { Status = WeighingStatus.Cancelled };
        History[id].Add(entry);
        return Task.CompletedTask;
    }

    public Task<int> ImportAsync(IReadOnlyList<(Weighing Weighing, AuditEntry Entry)> items, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        foreach (var (w, entry) in items)
        {
            var stored = w with { Id = Stored.Count + 1 };
            Stored.Add(stored);
            History[stored.Id] = [entry];
        }

        return Task.FromResult(items.Count);
    }

    public Task<IReadOnlyList<AuditEntry>> GetHistoryAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(History.TryGetValue(id, out var h) ? h : []);

    public Task<int?> FindTareKgAsync(string plate, CancellationToken ct = default) =>
        Task.FromResult(Tares.TryGetValue(plate, out var t) ? (int?)t : null);
}
