using System.Globalization;
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
    bool ScaleOnline,
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
    private readonly IScale _scale;
    private readonly IPlateRecognizer _recognizer;
    private readonly IPlatformIo _io;
    private readonly IWeighingRepository _repository;
    private readonly ITareProvider _tareProvider;
    private readonly Func<DateTimeOffset> _clock;

    private WeighingOptions _options;
    private StabilityTracker _tracker;
    private SessionState _state = SessionState.Idle;
    private PlatformState _platform;
    private WeightSample _sample;
    private DateTimeOffset? _lastSampleAt;
    private bool _stable;
    private PlateReading? _plate;
    private DateTimeOffset? _plateAt;
    private string? _manualPlate;
    private WeighingDetails _details = new();
    private DateTimeOffset? _readySince;
    private DateTimeOffset? _readyAt;
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
                return BuildSnapshot(DescribeState());
            }
        }
    }

    public WeighingOptions Options
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }
    }

    public void Dispose()
    {
        _scale.SampleReceived -= OnSample;
        _recognizer.PlateRecognized -= OnPlate;
        _io.StateChanged -= OnPlatformChanged;
    }

    /// <summary>Applies new settings; takes effect for the next evaluation (a running cycle is not restarted).</summary>
    public void UpdateOptions(WeighingOptions options) => Mutate(() =>
    {
        _options = options;
        _tracker = new StabilityTracker(options.StabilityToleranceKg, options.StabilityWindow);
        _stable = false;
    });

    /// <summary>
    /// Call periodically (about once a second). Without it a silent scale would leave the last
    /// "stable" verdict standing, because the session only reacts to incoming events.
    /// </summary>
    public void Tick() => Mutate(() => { });

    /// <summary>Overrides the recognised plate (operator typed it). Empty reverts to the camera.</summary>
    public void SetManualPlate(string? plate)
    {
        var normalized = PlateNumber.Normalize(plate);
        Mutate(() => _manualPlate = normalized.Length == 0 ? null : normalized);
    }

    public void SetDetails(WeighingDetails details) => Mutate(() => _details = details);

    /// <summary>Records the current stable weight, opens the barrier and lets the vehicle go.</summary>
    public Task<Weighing> CaptureAsync(CancellationToken ct = default) => CaptureCoreAsync(automatic: false, ct);

    private async Task<Weighing> CaptureCoreAsync(bool automatic, CancellationToken ct)
    {
        Weighing draft;
        var history = new List<AuditEntry>();
        lock (_gate)
        {
            if (_state != SessionState.ReadyToCapture || _capturing)
            {
                throw new InvalidOperationException("Weight is not ready to be captured.");
            }

            _capturing = true;
            var details = _details;
            var user = details.Operator ?? "";
            draft = new Weighing
            {
                Time = _clock(),
                Plate = EffectivePlate!,
                Trailer = Empty(details.Trailer),
                Driver = Empty(details.Driver),
                Counterparty = Empty(details.Counterparty),
                Cargo = Empty(details.Cargo),
                Kind = details.Kind,
                Operator = details.Operator,
                Status = _manualPlate is null ? WeighingStatus.Completed : WeighingStatus.ManualEdit,
                GrossKg = details.Kind == WeighingKind.Gross ? _sample.Kg : null,
                TareKg = details.Kind == WeighingKind.Tare ? _sample.Kg : null,
            };

            history.Add(_manualPlate is not null
                ? new AuditEntry(_plateAt ?? _clock(), user, $"Номер введён вручную: {PlateNumber.Format(_manualPlate)}")
                : new AuditEntry(_plateAt ?? _clock(), user,
                    $"Номер распознан: {PlateNumber.Format(_plate!.Value.Plate)} ({Math.Round(_plate.Value.Confidence * 100)}%, {_plate.Value.Camera})"));
            history.Add(new AuditEntry(_readyAt ?? _clock(), user,
                $"Вес стабилен: {_sample.Kg.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} кг"));
            history.Add(new AuditEntry(_clock(), user,
                automatic ? "Вес зафиксирован автоматически" : "Вес зафиксирован оператором"));
        }

        Weighing stored;
        try
        {
            if (draft.Kind == WeighingKind.Gross)
            {
                var tare = await _tareProvider.FindTareKgAsync(draft.Plate, ct).ConfigureAwait(false);
                draft = draft with { TareKg = tare, NetKg = tare is null ? null : draft.GrossKg - tare };
            }

            stored = await _repository.AddAsync(draft, history, ct).ConfigureAwait(false);
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

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool ScaleOnline => _lastSampleAt is { } at && _clock() - at <= _options.ScaleTimeout;

    private void OnSample(object? sender, WeightSample sample) => Mutate(() =>
    {
        // After a gap the old readings say nothing about the weight now: start the stability window over.
        if (_lastSampleAt is not null && !ScaleOnline)
        {
            _tracker.Reset();
        }

        _sample = sample;
        _lastSampleAt = _clock();
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
            _plate = reading with { Plate = PlateNumber.Normalize(reading.Plate) };
            _plateAt = _clock();
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
                    goto case SessionState.Entering;
                }

                if (CanCaptureNow())
                {
                    _state = SessionState.ReadyToCapture;
                    _readySince = _sample.Timestamp;
                    _readyAt = _clock();
                }

                break;

            case SessionState.ReadyToCapture:
                if (!_platform.FullyOnPlatform)
                {
                    _io.SetLight(TrafficLight.Green);
                    _state = SessionState.Entering;
                    _readySince = null;
                    // Nothing left at the entry either? Then the vehicle is gone and the cycle is over.
                    goto case SessionState.Entering;
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
            await CaptureCoreAsync(automatic: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // State changed between scheduling and running; the next evaluation will retry.
        }
        catch
        {
            // Storage failure: stay in ReadyToCapture, the operator sees the error when capturing by hand.
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
        && ScaleOnline
        && _sample.Kg >= _options.MinWeightKg
        && (!_options.RequirePlate || EffectivePlate is not null);

    // Must be called under _gate.
    private void ResetCycle()
    {
        _state = SessionState.Idle;
        _plate = null;
        _plateAt = null;
        _manualPlate = null;
        _readySince = null;
        _readyAt = null;
        _stable = false;
        _tracker.Reset();
        _io.SetLight(TrafficLight.Green);
    }

    private SessionSnapshot BuildSnapshot(string message) => new(
        _state,
        _sample.Kg,
        _stable && ScaleOnline,
        ScaleOnline,
        EffectivePlate,
        _manualPlate is null ? _plate?.Confidence : null,
        _platform,
        _io.Light,
        _io.Barrier,
        _state == SessionState.ReadyToCapture && !_capturing,
        message);

    private string DescribeState()
    {
        if (!ScaleOnline && _state is SessionState.Weighing or SessionState.ReadyToCapture or SessionState.Entering)
        {
            return "Нет данных от весов";
        }

        return _state switch
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
}
