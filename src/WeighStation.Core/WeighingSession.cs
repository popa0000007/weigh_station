using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Core;

public enum SessionState
{
    /// <summary>Platform is free, light is green.</summary>
    Idle,
    /// <summary>A vehicle is approaching or only partly on the platform.</summary>
    Entering,
    /// <summary>Vehicle is fully on the platform, light is red, waiting for plate and stable weight.</summary>
    Weighing,
    /// <summary>Weight is stable and plate is known: the operator (or auto-capture) can record it.</summary>
    ReadyToCapture,
    /// <summary>Weight is recorded, the barrier is open until the vehicle has left.</summary>
    Exiting,
}

public sealed record SessionSnapshot(
    SessionState State,
    int WeightKg,
    bool IsStable,
    string? Plate,
    double? PlateConfidence,
    PlatformState Platform,
    TrafficLight Light,
    BarrierPosition Barrier,
    bool CanCapture,
    string Message);

/// <summary>
/// The weighing cycle: sensors, scale and camera events in; light, barrier and stored weighings out.
/// Thread-safe: device events may arrive from any thread. <see cref="Changed"/> is raised outside the lock.
/// </summary>
public sealed class WeighingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly WeighingOptions _options;
    private readonly IScale _scale;
    private readonly IPlateRecognizer _recognizer;
    private readonly IPlatformIo _io;
    private readonly IWeighingRepository _repository;
    private readonly ITareProvider _tareProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly StabilityTracker _tracker;

    private SessionState _state = SessionState.Idle;
    private PlatformState _platform;
    private WeightSample _sample;
    private bool _stable;
    private PlateReading? _plate;
    private string? _manualPlate;
    private WeighingDetails _details = new();
    private DateTimeOffset? _readySince;
    private bool _capturing;
    private bool _autoCaptureQueued;

    public WeighingSession(
        WeighingOptions options,
        IScale scale,
        IPlateRecognizer recognizer,
        IPlatformIo io,
        IWeighingRepository repository,
        ITareProvider tareProvider,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _scale = scale;
        _recognizer = recognizer;
        _io = io;
        _repository = repository;
        _tareProvider = tareProvider;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _tracker = new StabilityTracker(options.StabilityToleranceKg, options.StabilityWindow);
        _platform = io.Current;

        _scale.SampleReceived += OnSample;
        _recognizer.PlateRecognized += OnPlate;
        _io.StateChanged += OnPlatformChanged;

        lock (_gate)
        {
            Evaluate();
        }
    }

    public event EventHandler<SessionSnapshot>? Changed;

    /// <summary>Raised after a weighing has been stored.</summary>
    public event EventHandler<Weighing>? WeighingCaptured;

    public SessionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshot("");
            }
        }
    }

    public void Dispose()
    {
        _scale.SampleReceived -= OnSample;
        _recognizer.PlateRecognized -= OnPlate;
        _io.StateChanged -= OnPlatformChanged;
    }

    /// <summary>Overrides the recognised plate (operator typed it). Null or empty reverts to the camera.</summary>
    public void SetManualPlate(string? plate)
    {
        Mutate(() => _manualPlate = string.IsNullOrWhiteSpace(plate) ? null : plate.Trim().ToUpperInvariant());
    }

    public void SetDetails(WeighingDetails details) => Mutate(() => _details = details);

    /// <summary>Records the current stable weight, opens the barrier and lets the vehicle go.</summary>
    public async Task<Weighing> CaptureAsync(CancellationToken ct = default)
    {
        Weighing draft;
        WeighingDetails details;
        lock (_gate)
        {
            if (_state != SessionState.ReadyToCapture || _capturing)
            {
                throw new InvalidOperationException("Weight is not ready to be captured.");
            }

            _capturing = true;
            details = _details;
            draft = new Weighing
            {
                Time = _clock(),
                Plate = EffectivePlate!,
                Trailer = details.Trailer,
                Driver = details.Driver,
                Counterparty = details.Counterparty,
                Cargo = details.Cargo,
                Kind = details.Kind,
                Operator = details.Operator,
                Status = _manualPlate is null ? WeighingStatus.Completed : WeighingStatus.ManualEdit,
            };
            draft = draft with { GrossKg = details.Kind == WeighingKind.Gross ? _sample.Kg : null };
            draft = draft with { TareKg = details.Kind == WeighingKind.Tare ? _sample.Kg : null };
        }

        Weighing stored;
        try
        {
            if (draft.Kind == WeighingKind.Gross)
            {
                var tare = await _tareProvider.FindTareKgAsync(draft.Plate, ct).ConfigureAwait(false);
                draft = draft with { TareKg = tare, NetKg = tare is null ? null : draft.GrossKg - tare };
            }

            stored = await _repository.AddAsync(draft, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _capturing = false;
            }

            throw;
        }

        lock (_gate)
        {
            _capturing = false;
            _state = SessionState.Exiting;
            _readySince = null;
            _io.SetBarrier(BarrierPosition.Open);
        }

        WeighingCaptured?.Invoke(this, stored);
        Publish();
        return stored;
    }

    /// <summary>Abandons the current weighing without recording it and releases the vehicle.</summary>
    public void Cancel()
    {
        Mutate(() =>
        {
            if (_state is SessionState.Weighing or SessionState.ReadyToCapture or SessionState.Entering)
            {
                if (VehicleHasLeft())
                {
                    ResetCycle();
                    return;
                }

                _state = SessionState.Exiting;
                _readySince = null;
                _io.SetBarrier(BarrierPosition.Open);
            }
        });
    }

    /// <summary>Manual barrier control. Refuses to close while the exit sensor is occupied.</summary>
    public bool TrySetBarrier(BarrierPosition position)
    {
        var allowed = true;
        Mutate(() =>
        {
            if (position == BarrierPosition.Closed && _platform.ExitOccupied)
            {
                allowed = false;
                return;
            }

            _io.SetBarrier(position);
        });
        return allowed;
    }

    private string? EffectivePlate => _manualPlate ?? _plate?.Plate;

    private void OnSample(object? sender, WeightSample sample) => Mutate(() =>
    {
        _sample = sample;
        var settled = _tracker.Add(sample);
        _stable = settled && (!_options.RequireTerminalStable || sample.IsStable);
    });

    private void OnPlate(object? sender, PlateReading reading) => Mutate(() =>
    {
        if (_state is SessionState.Idle or SessionState.Exiting || reading.Confidence < _options.MinPlateConfidence)
        {
            return;
        }

        if (_plate is null || reading.Confidence >= _plate.Value.Confidence)
        {
            _plate = reading;
        }
    });

    private void OnPlatformChanged(object? sender, PlatformState state) => Mutate(() => _platform = state);

    private void Mutate(Action change)
    {
        lock (_gate)
        {
            change();
            Evaluate();
        }

        Publish();
    }

    private void Publish()
    {
        SessionSnapshot snapshot;
        lock (_gate)
        {
            snapshot = BuildSnapshot(DescribeState());
        }

        Changed?.Invoke(this, snapshot);
    }

    // Must be called under _gate.
    private void Evaluate()
    {
        switch (_state)
        {
            case SessionState.Idle:
                if (_platform.EntryOccupied || _platform.FullyOnPlatform)
                {
                    _state = SessionState.Entering;
                    goto case SessionState.Entering;
                }

                _io.SetLight(TrafficLight.Green);
                break;

            case SessionState.Entering:
                if (_platform.FullyOnPlatform)
                {
                    _io.SetLight(TrafficLight.Red);
                    _tracker.Reset();
                    _stable = false;
                    _state = SessionState.Weighing;
                    goto case SessionState.Weighing;
                }

                if (!_platform.EntryOccupied)
                {
                    ResetCycle();
                }

                break;

            case SessionState.Weighing:
                if (!_platform.FullyOnPlatform)
                {
                    _io.SetLight(TrafficLight.Green);
                    _state = SessionState.Entering;
                    break;
                }

                if (CanCaptureNow())
                {
                    _state = SessionState.ReadyToCapture;
                    _readySince = _sample.Timestamp;
                }

                break;

            case SessionState.ReadyToCapture:
                if (!_platform.FullyOnPlatform)
                {
                    _io.SetLight(TrafficLight.Green);
                    _state = SessionState.Entering;
                    _readySince = null;
                }
                else if (!CanCaptureNow())
                {
                    _state = SessionState.Weighing;
                    _readySince = null;
                }
                else if (_options.AutoCapture && !_capturing && !_autoCaptureQueued && _readySince is { } since
                         && _sample.Timestamp - since >= _options.AutoCaptureDelay)
                {
                    _autoCaptureQueued = true;
                    _ = Task.Run(AutoCaptureAsync);
                }

                break;

            case SessionState.Exiting:
                // A queued vehicle on the entry sensor must not keep the barrier open forever.
                if (VehicleHasLeft())
                {
                    _io.SetBarrier(BarrierPosition.Closed);
                    ResetCycle();
                    // A vehicle may already be waiting on the entry sensor.
                    goto case SessionState.Idle;
                }

                break;
        }
    }

    private async Task AutoCaptureAsync()
    {
        try
        {
            await CaptureAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // State changed between scheduling and running; the next evaluation will retry.
        }
        finally
        {
            lock (_gate)
            {
                _autoCaptureQueued = false;
            }
        }
    }

    private bool VehicleHasLeft() => !_platform.FullyOnPlatform && !_platform.ExitOccupied;

    private bool CanCaptureNow() =>
        _stable
        && _sample.Kg >= _options.MinWeightKg
        && (!_options.RequirePlate || EffectivePlate is not null);

    // Must be called under _gate.
    private void ResetCycle()
    {
        _state = SessionState.Idle;
        _plate = null;
        _manualPlate = null;
        _readySince = null;
        _stable = false;
        _tracker.Reset();
        _io.SetLight(TrafficLight.Green);
    }

    private SessionSnapshot BuildSnapshot(string message) => new(
        _state,
        _sample.Kg,
        _stable,
        EffectivePlate,
        _manualPlate is null ? _plate?.Confidence : null,
        _platform,
        _io.Light,
        _io.Barrier,
        _state == SessionState.ReadyToCapture && !_capturing,
        message);

    private string DescribeState() => _state switch
    {
        SessionState.Idle => "Платформа свободна",
        SessionState.Entering => "Заезд на платформу",
        SessionState.Weighing when EffectivePlate is null => "Ожидание распознавания номера",
        SessionState.Weighing => "Ожидание стабильного веса",
        SessionState.ReadyToCapture => "Вес стабилен — можно фиксировать",
        SessionState.Exiting => "Вес записан, выезд свободен",
        _ => "",
    };
}
