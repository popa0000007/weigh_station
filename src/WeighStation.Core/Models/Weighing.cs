namespace WeighStation.Core.Models;

public enum WeighingKind
{
    /// <summary>Loaded vehicle: the measured weight is gross, tare comes from the reference book.</summary>
    Gross,
    /// <summary>Empty vehicle: the measured weight is stored as tare for later gross weighings.</summary>
    Tare,
}

public enum WeighingStatus
{
    Completed,
    ManualEdit,
    Cancelled,
}

public sealed record Weighing
{
    public long Id { get; init; }
    public DateTimeOffset Time { get; init; }
    public required string Plate { get; init; }
    public string? Trailer { get; init; }
    public string? Driver { get; init; }
    public string? Counterparty { get; init; }
    public string? Cargo { get; init; }
    public WeighingKind Kind { get; init; }
    public int? GrossKg { get; init; }
    public int? TareKg { get; init; }
    public int? NetKg { get; init; }
    public WeighingStatus Status { get; init; }
    public string? Operator { get; init; }
}

/// <summary>Operator-entered data that accompanies a weighing.</summary>
public sealed record WeighingDetails
{
    public string? Trailer { get; init; }
    public string? Driver { get; init; }
    public string? Counterparty { get; init; }
    public string? Cargo { get; init; }
    public WeighingKind Kind { get; init; } = WeighingKind.Gross;
    public string? Operator { get; init; }
}
