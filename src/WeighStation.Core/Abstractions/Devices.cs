using WeighStation.Core.Models;

namespace WeighStation.Core.Abstractions;

/// <summary>Weighing terminal / SIWAREX. Raises a sample on every update (typically 5–20 Hz).</summary>
public interface IScale
{
    event EventHandler<WeightSample>? SampleReceived;
}

/// <summary>ANPR camera(s). Raises an event for every recognised plate.</summary>
public interface IPlateRecognizer
{
    event EventHandler<PlateReading>? PlateRecognized;
}

/// <summary>PLC I/O: platform sensors in, traffic light and barrier out.</summary>
public interface IPlatformIo
{
    PlatformState Current { get; }
    TrafficLight Light { get; }
    BarrierPosition Barrier { get; }
    event EventHandler<PlatformState>? StateChanged;
    void SetLight(TrafficLight light);
    void SetBarrier(BarrierPosition position);
}
