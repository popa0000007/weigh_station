namespace WeighStation.Core;

public sealed record WeighingOptions
{
    /// <summary>Weights below this are treated as "nothing on the platform" and can't be captured.</summary>
    public int MinWeightKg { get; init; } = 500;
    public int StabilityToleranceKg { get; init; } = 20;
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Also require the terminal's own "stable" flag (recommended for legal-for-trade scales).</summary>
    public bool RequireTerminalStable { get; init; } = true;
    public bool RequirePlate { get; init; } = true;
    public double MinPlateConfidence { get; init; } = 0.8;
    public bool AutoCapture { get; init; }
    public TimeSpan AutoCaptureDelay { get; init; } = TimeSpan.FromSeconds(5);
}
