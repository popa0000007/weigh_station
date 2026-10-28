using WeighStation.Core.Models;

namespace WeighStation.Core.Tests;

public class WeighingSessionTests
{
    private readonly FakeDevices _dev = new();
    private readonly FakeRepository _repo = new();
    private WeighingSession _session;

    public WeighingSessionTests() => _session = Create(new WeighingOptions());

    private WeighingSession Create(WeighingOptions options) =>
        new(options, _dev, _dev, _dev, _repo, _repo, () => DateTimeOffset.UnixEpoch);

    /// <summary>Truck drives on, plate recognised, weight settled for several seconds.</summary>
    private void TruckOnPlatformSettled(int kg = 42780, string plate = "А123ВС197")
    {
        _dev.Sensors(entry: true);
        _dev.Plate(plate);
        _dev.Sensors(onPlatform: true);
        _dev.Hold(kg, 0, 3);
    }

    [Fact]
    public void Starts_idle_with_green_light_and_closed_barrier()
    {
        var s = _session.Snapshot;
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Equal(TrafficLight.Green, _dev.Light);
        Assert.Equal(BarrierPosition.Closed, _dev.Barrier);
    }

    [Fact]
    public void Vehicle_fully_on_platform_turns_light_red()
    {
        _dev.Sensors(entry: true);
        Assert.Equal(SessionState.Entering, _session.Snapshot.State);
        Assert.Equal(TrafficLight.Green, _dev.Light);

        _dev.Sensors(onPlatform: true);
        Assert.Equal(SessionState.Weighing, _session.Snapshot.State);
        Assert.Equal(TrafficLight.Red, _dev.Light);
    }

    [Fact]
    public void Stable_weight_with_plate_is_ready_to_capture()
    {
        TruckOnPlatformSettled();
        var s = _session.Snapshot;
        Assert.Equal(SessionState.ReadyToCapture, s.State);
        Assert.True(s.CanCapture);
        Assert.Equal("А123ВС197", s.Plate);
        Assert.Equal(42780, s.WeightKg);
    }

    [Fact]
    public void Not_ready_while_terminal_reports_unstable()
    {
        _dev.Sensors(onPlatform: true);
        _dev.Plate("А123ВС197");
        _dev.Hold(42780, 0, 3, stable: false);
        Assert.Equal(SessionState.Weighing, _session.Snapshot.State);
        Assert.False(_session.Snapshot.CanCapture);
    }

    [Fact]
    public void Not_ready_without_plate_until_operator_types_one()
    {
        _dev.Sensors(onPlatform: true);
        _dev.Hold(42780, 0, 3);
        Assert.Equal(SessionState.Weighing, _session.Snapshot.State);

        _session.SetManualPlate(" в777вв 77 ");
        Assert.Equal(SessionState.ReadyToCapture, _session.Snapshot.State);
        Assert.Equal("В777ВВ 77", _session.Snapshot.Plate);
    }

    [Fact]
    public void Low_confidence_plate_is_ignored()
    {
        _dev.Sensors(onPlatform: true);
        _dev.Plate("А123ВС197", confidence: 0.5);
        _dev.Hold(42780, 0, 3);
        Assert.Null(_session.Snapshot.Plate);
        Assert.Equal(SessionState.Weighing, _session.Snapshot.State);
    }

    [Fact]
    public void Weight_below_minimum_cannot_be_captured()
    {
        _dev.Sensors(onPlatform: true);
        _dev.Plate("А123ВС197");
        _dev.Hold(100, 0, 3);
        Assert.Equal(SessionState.Weighing, _session.Snapshot.State);
    }

    [Fact]
    public async Task Capture_stores_weighing_opens_barrier_and_waits_for_the_vehicle_to_leave()
    {
        TruckOnPlatformSettled();

        var stored = await _session.CaptureAsync();

        Assert.Equal(42780, stored.GrossKg);
        Assert.Equal("А123ВС197", stored.Plate);
        Assert.Single(_repo.Stored);
        Assert.Equal(SessionState.Exiting, _session.Snapshot.State);
        Assert.Equal(BarrierPosition.Open, _dev.Barrier);
        Assert.Equal(TrafficLight.Red, _dev.Light);

        _dev.Sensors(exit: true);
        Assert.Equal(BarrierPosition.Open, _dev.Barrier);
        Assert.Equal(SessionState.Exiting, _session.Snapshot.State);

        _dev.Sensors();
        Assert.Equal(SessionState.Idle, _session.Snapshot.State);
        Assert.Equal(BarrierPosition.Closed, _dev.Barrier);
        Assert.Equal(TrafficLight.Green, _dev.Light);
        Assert.Null(_session.Snapshot.Plate);
    }

    [Fact]
    public async Task Queued_vehicle_on_entry_sensor_does_not_keep_the_barrier_open()
    {
        TruckOnPlatformSettled();
        await _session.CaptureAsync();

        _dev.Sensors(entry: true, exit: true);
        _dev.Sensors(entry: true);

        Assert.Equal(BarrierPosition.Closed, _dev.Barrier);
        Assert.Equal(SessionState.Entering, _session.Snapshot.State);
    }

    [Fact]
    public async Task Gross_weighing_computes_net_from_known_tare()
    {
        _repo.Tares["А123ВС197"] = 14320;
        TruckOnPlatformSettled();

        var stored = await _session.CaptureAsync();

        Assert.Equal(14320, stored.TareKg);
        Assert.Equal(28460, stored.NetKg);
    }

    [Fact]
    public async Task Gross_weighing_of_unknown_vehicle_has_no_net()
    {
        TruckOnPlatformSettled();
        var stored = await _session.CaptureAsync();
        Assert.Null(stored.TareKg);
        Assert.Null(stored.NetKg);
    }

    [Fact]
    public async Task Tare_weighing_stores_weight_as_tare()
    {
        _session.SetDetails(new WeighingDetails { Kind = WeighingKind.Tare });
        TruckOnPlatformSettled(14320);

        var stored = await _session.CaptureAsync();

        Assert.Null(stored.GrossKg);
        Assert.Equal(14320, stored.TareKg);
    }

    [Fact]
    public async Task Manually_typed_plate_marks_weighing_as_manual_edit()
    {
        _dev.Sensors(onPlatform: true);
        _session.SetManualPlate("В777ВВ77");
        _dev.Hold(30000, 0, 3);

        var stored = await _session.CaptureAsync();

        Assert.Equal(WeighingStatus.ManualEdit, stored.Status);
    }

    [Fact]
    public async Task Capture_is_refused_when_not_ready()
    {
        _dev.Sensors(onPlatform: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.CaptureAsync());
        Assert.Empty(_repo.Stored);
    }

    [Fact]
    public async Task Failed_save_keeps_barrier_closed_and_allows_retry()
    {
        TruckOnPlatformSettled();
        _repo.FailWith = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() => _session.CaptureAsync());
        Assert.Equal(BarrierPosition.Closed, _dev.Barrier);
        Assert.Equal(SessionState.ReadyToCapture, _session.Snapshot.State);

        _repo.FailWith = null;
        await _session.CaptureAsync();
        Assert.Single(_repo.Stored);
    }

    [Fact]
    public void Vehicle_rolling_off_before_capture_returns_to_entering_with_green_light()
    {
        TruckOnPlatformSettled();
        _dev.Sensors(entry: false, onPlatform: false, exit: true);
        Assert.Equal(SessionState.Entering, _session.Snapshot.State);
        Assert.Equal(TrafficLight.Green, _dev.Light);
    }

    [Fact]
    public void Cancel_releases_vehicle_without_storing()
    {
        TruckOnPlatformSettled();
        _session.Cancel();
        Assert.Equal(SessionState.Exiting, _session.Snapshot.State);
        Assert.Equal(BarrierPosition.Open, _dev.Barrier);
        Assert.Empty(_repo.Stored);
    }

    [Fact]
    public void Barrier_cannot_be_closed_manually_while_exit_sensor_is_occupied()
    {
        _dev.Sensors(exit: true);
        _session.TrySetBarrier(BarrierPosition.Open);
        Assert.False(_session.TrySetBarrier(BarrierPosition.Closed));
        Assert.Equal(BarrierPosition.Open, _dev.Barrier);

        _dev.Sensors();
        Assert.True(_session.TrySetBarrier(BarrierPosition.Closed));
        Assert.Equal(BarrierPosition.Closed, _dev.Barrier);
    }

    [Fact]
    public async Task Auto_capture_records_weight_after_delay()
    {
        _session.Dispose();
        _session = Create(new WeighingOptions { AutoCapture = true, AutoCaptureDelay = TimeSpan.FromSeconds(5) });
        _dev.Sensors(onPlatform: true);
        _dev.Plate("А123ВС197");
        _dev.Hold(42780, 0, 10);

        for (var i = 0; i < 100 && _repo.Stored.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(_repo.Stored);
        Assert.Equal(SessionState.Exiting, _session.Snapshot.State);
    }

    [Fact]
    public void Changed_event_reports_state_transitions()
    {
        var states = new List<SessionState>();
        _session.Changed += (_, s) => states.Add(s.State);
        TruckOnPlatformSettled();
        Assert.Contains(SessionState.Entering, states);
        Assert.Contains(SessionState.Weighing, states);
        Assert.Equal(SessionState.ReadyToCapture, states[^1]);
    }
}
