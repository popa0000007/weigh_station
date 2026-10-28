using WeighStation.Core.Models;

namespace WeighStation.Core.Tests;

public class WeighingSessionSafetyTests
{
    private readonly FakeDevices _dev = new();
    private readonly FakeRepository _repo = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly WeighingSession _session;

    public WeighingSessionSafetyTests()
    {
        _session = new WeighingSession(new WeighingOptions(), _dev, _dev, _dev, _repo, _repo, () => _now);
    }

    private void SettledTruck()
    {
        _dev.Sensors(onPlatform: true);
        _dev.Plate("А123ВС197");
        _dev.Hold(42780, 0, 3);
    }

    [Fact]
    public void Scale_is_offline_until_the_first_sample()
    {
        Assert.False(_session.Snapshot.ScaleOnline);
        _dev.Sample(0, 0);
        Assert.True(_session.Snapshot.ScaleOnline);
    }

    [Fact]
    public void Silent_scale_blocks_capturing_after_the_timeout_even_though_nothing_else_happened()
    {
        SettledTruck();
        Assert.True(_session.Snapshot.CanCapture);

        _now += TimeSpan.FromSeconds(10);
        _session.Tick();

        var s = _session.Snapshot;
        Assert.False(s.ScaleOnline);
        Assert.False(s.CanCapture);
        Assert.False(s.IsStable);
        Assert.Equal(SessionState.Weighing, s.State);
        Assert.Equal("Нет данных от весов", s.Message);
    }

    [Fact]
    public async Task Scale_coming_back_needs_a_fresh_stable_window_before_capture()
    {
        SettledTruck();
        _now += TimeSpan.FromSeconds(10);
        _session.Tick();

        _dev.Sample(42780, 10);
        Assert.False(_session.Snapshot.CanCapture);

        _dev.Hold(42780, 10.5, 13);
        Assert.True(_session.Snapshot.CanCapture);
        await _session.CaptureAsync();
    }

    [Fact]
    public void Tick_without_changes_does_not_disturb_a_healthy_cycle()
    {
        SettledTruck();
        _session.Tick();
        _session.Tick();
        Assert.Equal(SessionState.ReadyToCapture, _session.Snapshot.State);
    }

    [Fact]
    public void Changed_event_fires_on_tick_so_the_ui_learns_about_a_dead_scale()
    {
        SettledTruck();
        var last = _session.Snapshot;
        _session.Changed += (_, s) => last = s;

        _now += TimeSpan.FromSeconds(10);
        _session.Tick();

        Assert.False(last.ScaleOnline);
    }

    [Fact]
    public void New_options_apply_without_restarting()
    {
        SettledTruck();
        Assert.True(_session.Snapshot.CanCapture);

        _session.UpdateOptions(new WeighingOptions { MinWeightKg = 50_000 });
        _dev.Hold(42780, 3.5, 8);

        Assert.Equal(50_000, _session.Options.MinWeightKg);
        Assert.False(_session.Snapshot.CanCapture);
    }

    [Fact]
    public async Task Latin_letters_from_the_camera_are_stored_as_cyrillic()
    {
        _dev.Sensors(onPlatform: true);
        _dev.Plate("A123BC197");
        _dev.Hold(42780, 0, 3);

        var stored = await _session.CaptureAsync();

        Assert.Equal("А123ВС197", stored.Plate);
    }

    [Fact]
    public async Task Capture_writes_the_story_of_the_weighing_to_the_history()
    {
        _session.SetDetails(new WeighingDetails { Operator = "ivan" });
        SettledTruck();

        var stored = await _session.CaptureAsync();

        var history = _repo.History[stored.Id];
        Assert.Equal(3, history.Count);
        Assert.StartsWith("Номер распознан: А123ВС 197 (95%", history[0].Text);
        Assert.StartsWith("Вес стабилен: 42", history[1].Text);
        Assert.Equal("Вес зафиксирован оператором", history[2].Text);
        Assert.All(history, h => Assert.Equal("ivan", h.User));
    }

    [Fact]
    public async Task Manual_plate_is_recorded_as_such_in_the_history()
    {
        _dev.Sensors(onPlatform: true);
        _session.SetManualPlate("в777вв 77");
        _dev.Hold(30000, 0, 3);

        var stored = await _session.CaptureAsync();

        Assert.Equal("Номер введён вручную: В777ВВ 77", _repo.History[stored.Id][0].Text);
    }

    [Fact]
    public async Task Blank_details_are_stored_as_null()
    {
        _session.SetDetails(new WeighingDetails { Driver = "  ", Cargo = " Щебень " });
        SettledTruck();

        var stored = await _session.CaptureAsync();

        Assert.Null(stored.Driver);
        Assert.Equal("Щебень", stored.Cargo);
    }

    [Fact]
    public async Task Auto_capture_is_marked_in_the_history()
    {
        var session = new WeighingSession(
            new WeighingOptions { AutoCapture = true, AutoCaptureDelay = TimeSpan.FromSeconds(3) },
            _dev, _dev, _dev, _repo, _repo, () => _now);
        _dev.Sensors(onPlatform: true);
        _dev.Plate("А123ВС197");
        _dev.Hold(42780, 0, 10);

        for (var i = 0; i < 100 && _repo.Stored.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("Вес зафиксирован автоматически", _repo.History[1][^1].Text);
        session.Dispose();
    }
}
