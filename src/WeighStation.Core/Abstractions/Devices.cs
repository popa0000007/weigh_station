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

public enum DeviceHealth { Online, Offline }

public sealed record DeviceStatus(string Name, DeviceHealth Health, string Detail);

/// <summary>Link status of each piece of equipment, for the status bar and the "Devices" page.</summary>
public interface IDeviceMonitor
{
    IReadOnlyList<DeviceStatus> GetStatuses();
    event EventHandler? StatusChanged;
}

/// <summary>Only the simulator implements this: lets the operator break things on purpose to see how the app reacts.</summary>
public interface ISimulationControl
{
    bool AutoTrucks { get; set; }
    bool ScaleConnected { get; set; }
    bool CamerasConnected { get; set; }
    event EventHandler? Changed;
}
