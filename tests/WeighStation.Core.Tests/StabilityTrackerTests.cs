using WeighStation.Core.Models;

namespace WeighStation.Core.Tests;

public class StabilityTrackerTests
{
    private static WeightSample S(int kg, double t) => new(kg, true, DateTimeOffset.UnixEpoch.AddSeconds(t));

    [Fact]
    public void Becomes_stable_after_window_within_tolerance()
    {
        var tracker = new StabilityTracker(20, TimeSpan.FromSeconds(2));
        Assert.False(tracker.Add(S(1000, 0)));
        Assert.False(tracker.Add(S(1005, 1)));
        Assert.False(tracker.Add(S(995, 1.9)));
        Assert.True(tracker.Add(S(1000, 2)));
    }

    [Fact]
    public void Jump_beyond_tolerance_restarts_the_window()
    {
        var tracker = new StabilityTracker(20, TimeSpan.FromSeconds(2));
        tracker.Add(S(1000, 0));
        Assert.True(tracker.Add(S(1000, 2)));
        Assert.False(tracker.Add(S(1100, 2.5)));
        Assert.False(tracker.Add(S(1100, 4.4)));
        Assert.True(tracker.Add(S(1100, 4.5)));
    }

    [Fact]
    public void Slow_drift_does_not_count_as_stable()
    {
        var tracker = new StabilityTracker(20, TimeSpan.FromSeconds(2));
        var stable = false;
        for (var i = 0; i < 10; i++)
        {
            stable = tracker.Add(S(1000 + i * 10, i * 0.5));
        }

        Assert.False(stable);
    }

    [Fact]
    public void Reset_requires_a_new_full_window()
    {
        var tracker = new StabilityTracker(20, TimeSpan.FromSeconds(2));
        tracker.Add(S(1000, 0));
        Assert.True(tracker.Add(S(1000, 2)));
        tracker.Reset();
        Assert.False(tracker.Add(S(1000, 3)));
    }
}
