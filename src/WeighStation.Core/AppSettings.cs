namespace WeighStation.Core;

public sealed record AppSettings
{
    /// <summary>Shown in the journal history. Empty means "use the Windows user name".</summary>
    public string OperatorName { get; init; } = "";
    public WeighingOptions Weighing { get; init; } = new();
    public DeviceSettings Devices { get; init; } = new();
}

/// <summary>Connection parameters of the real equipment. Used once the hardware drivers are plugged in.</summary>
public sealed record DeviceSettings
{
    public string PlcHost { get; init; } = "192.168.0.10";
    public int PlcRack { get; init; }
    public int PlcSlot { get; init; } = 1;
    public string ScaleHost { get; init; } = "192.168.0.20";
    public int ScalePort { get; init; } = 502;
    public string Camera1Url { get; init; } = "rtsp://192.168.0.31/stream1";
    public string Camera2Url { get; init; } = "rtsp://192.168.0.32/stream1";
}
