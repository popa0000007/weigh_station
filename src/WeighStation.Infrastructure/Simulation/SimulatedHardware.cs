using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Infrastructure.Simulation;

/// <summary>
/// Stand-in for the PLC, scale terminal and ANPR cameras so the application can run without hardware.
/// Plays an endless loop: truck arrives, is recognised, drives onto the platform, weight settles,
/// then it waits for the barrier to open, leaves and the cycle repeats.
/// Replace by real <see cref="IScale"/>, <see cref="IPlatformIo"/> and <see cref="IPlateRecognizer"/> implementations.
/// </summary>
public sealed class SimulatedHardware : IScale, IPlateRecognizer, IPlatformIo, IDeviceMonitor, ISimulationControl
{
    private static readonly (string Plate, int Kg)[] Trucks =
    [
        ("А123ВС197", 42780), ("К507МР50", 38940), ("Н880ОТ77", 41220), ("Е412КХ197", 44060),
    ];

    private readonly double _speed;
    private readonly Random _random = new(1);
    private readonly object _gate = new();
    private PlatformState _state;
    private int _currentKg;
    private int _targetKg;
    private int _settledTicks;
    private volatile bool _autoTrucks = true;
    private volatile bool _scaleConnected = true;
    private volatile bool _camerasConnected = true;
    private volatile TrafficLight _light = TrafficLight.Green;
    private volatile BarrierPosition _barrier = BarrierPosition.Closed;

    /// <param name="speed">Time multiplier; 1 = real time, 20 = twenty times faster (used by tests).</param>
    public SimulatedHardware(double speed = 1) => _speed = speed;

    public event EventHandler<WeightSample>? SampleReceived;
    public event EventHandler<PlateReading>? PlateRecognized;
    public event EventHandler<PlatformState>? StateChanged;
    public event EventHandler? StatusChanged;
    public event EventHandler? Changed;

    public PlatformState Current
    {
        get { lock (_gate) { return _state; } }
    }

    public TrafficLight Light => _light;

    public BarrierPosition Barrier => _barrier;

    public bool AutoTrucks
    {
        get => _autoTrucks;
        set
        {
            if (value == _autoTrucks)
            {
                return;
            }

            _autoTrucks = value;
            Notify();
        }
    }

    public bool ScaleConnected
    {
        get => _scaleConnected;
        set
        {
            if (value == _scaleConnected)
            {
                return;
            }

            _scaleConnected = value;
            Notify();
        }
    }

    public bool CamerasConnected
    {
        get => _camerasConnected;
        set
        {
            if (value == _camerasConnected)
            {
                return;
            }

            _camerasConnected = value;
            Notify();
        }
    }

    public void SetLight(TrafficLight light) => _light = light;

    public void SetBarrier(BarrierPosition position) => _barrier = position;

    public IReadOnlyList<DeviceStatus> GetStatuses() =>
    [
        new("ПЛК", DeviceHealth.Online, "Симулятор"),
        Status("Терминал весов", _scaleConnected),
        Status("Камера 1", _camerasConnected),
        Status("Камера 2", _camerasConnected),
    ];

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(ScaleLoopAsync(ct), TruckLoopAsync(ct));

    private static DeviceStatus Status(string name, bool connected) =>
        new(name, connected ? DeviceHealth.Online : DeviceHealth.Offline, connected ? "Симулятор" : "Нет связи (симуляция отказа)");

    private void Notify()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ScaleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Delay(100));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!_scaleConnected)
                {
                    continue;
                }

                int kg;
                bool stable;
                lock (_gate)
                {
                    const int step = 1500;
                    var delta = Math.Clamp(_targetKg - _currentKg, -step, step);
                    _currentKg += delta;
                    var reached = _currentKg == _targetKg;
                    _settledTicks = reached ? _settledTicks + 1 : 0;
                    stable = reached && _settledTicks >= 5;
                    kg = reached ? _currentKg + _random.Next(-5, 6) : _currentKg + _random.Next(-80, 81);
                }

                SampleReceived?.Invoke(this, new WeightSample(Math.Max(0, kg), stable, DateTimeOffset.Now));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TruckLoopAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; !ct.IsCancellationRequested; i++)
            {
                var (plate, kg) = Trucks[i % Trucks.Length];
                await Task.Delay(Delay(3000), ct);
                while (!_autoTrucks)
                {
                    await Task.Delay(Delay(100), ct);
                }

                Set(s => s with { EntryOccupied = true });
                await Task.Delay(Delay(1500), ct);
                if (_camerasConnected)
                {
                    PlateRecognized?.Invoke(this, new PlateReading(plate, 0.96, "Камера 1", DateTimeOffset.Now));
                }

                await Task.Delay(Delay(1500), ct);

                lock (_gate)
                {
                    _targetKg = kg;
                }

                Set(s => s with { EntryOccupied = false, FullyOnPlatform = true });

                while (_barrier != BarrierPosition.Open)
                {
                    await Task.Delay(Delay(50), ct);
                }

                await Task.Delay(Delay(1000), ct);
                lock (_gate)
                {
                    _targetKg = 0;
                }

                Set(s => s with { FullyOnPlatform = false, ExitOccupied = true });
                await Task.Delay(Delay(2000), ct);
                Set(s => s with { ExitOccupied = false });

                while (_light != TrafficLight.Green)
                {
                    await Task.Delay(Delay(50), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Set(Func<PlatformState, PlatformState> change)
    {
        PlatformState next;
        lock (_gate)
        {
            _state = next = change(_state);
        }

        StateChanged?.Invoke(this, next);
    }

    private TimeSpan Delay(int ms) => TimeSpan.FromMilliseconds(ms / _speed);
}
