namespace WeighStation.Core.Models;

/// <summary>One reading from the scale terminal. Weight is in kilograms.</summary>
public readonly record struct WeightSample(int Kg, bool IsStable, DateTimeOffset Timestamp);

public readonly record struct PlateReading(string Plate, double Confidence, string Camera, DateTimeOffset Timestamp);

/// <summary>Discrete inputs of the platform (photo barriers / induction loops).</summary>
public readonly record struct PlatformState(bool EntryOccupied, bool FullyOnPlatform, bool ExitOccupied)
{
    public bool IsClear => !EntryOccupied && !FullyOnPlatform && !ExitOccupied;
}

public enum TrafficLight { Red, Green }

public enum BarrierPosition { Closed, Open }
