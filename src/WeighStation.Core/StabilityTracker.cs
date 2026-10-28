using WeighStation.Core.Models;

namespace WeighStation.Core;

/// <summary>
/// Decides whether the weight has settled: all samples since the anchor stay within a tolerance
/// band for at least the configured window.
/// </summary>
public sealed class StabilityTracker(int toleranceKg, TimeSpan window)
{
    private DateTimeOffset _anchor;
    private int _min;
    private int _max;
    private bool _hasAnchor;

    public bool Add(WeightSample sample)
    {
        if (!_hasAnchor)
        {
            Anchor(sample);
            return false;
        }

        var min = Math.Min(_min, sample.Kg);
        var max = Math.Max(_max, sample.Kg);
        if (max - min > toleranceKg)
        {
            Anchor(sample);
            return false;
        }

        _min = min;
        _max = max;
        return sample.Timestamp - _anchor >= window;
    }

    public void Reset() => _hasAnchor = false;

    private void Anchor(WeightSample sample)
    {
        _anchor = sample.Timestamp;
        _min = _max = sample.Kg;
        _hasAnchor = true;
    }
}
