namespace WeighStation.Core.Models;

/// <summary>A vehicle known to the weighbridge. Fields other than the plate prefill the weighing form.</summary>
public sealed record Vehicle
{
    public long Id { get; init; }
    public required string Plate { get; init; }
    public string? Trailer { get; init; }
    public string? Driver { get; init; }
    public string? Company { get; init; }
    public string? Cargo { get; init; }
    /// <summary>Tare to use when the vehicle has no tare weighing yet.</summary>
    public int? TareKg { get; init; }
    /// <summary>Maximum permitted gross mass; the weighing screen warns when it is exceeded.</summary>
    public int? MaxMassKg { get; init; }
    public string? Note { get; init; }
}

public sealed record Company
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? Inn { get; init; }
    public string? Note { get; init; }
}

public sealed record CargoType
{
    public long Id { get; init; }
    public required string Name { get; init; }
}

public sealed record Driver
{
    public long Id { get; init; }
    public required string FullName { get; init; }
    public string? Phone { get; init; }
}

public sealed record AuditEntry(DateTimeOffset Time, string User, string Text);
