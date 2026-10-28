using WeighStation.Core.Abstractions;

namespace WeighStation.Infrastructure.Persistence;

/// <summary>Tare from the latest tare weighing; if the vehicle was never weighed empty, the value typed into the reference book.</summary>
public sealed class ReferenceTareProvider(ITareProvider journal, IVehicleStore vehicles) : ITareProvider
{
    public async Task<int?> FindTareKgAsync(string plate, CancellationToken ct = default) =>
        await journal.FindTareKgAsync(plate, ct) ?? (await vehicles.FindByPlateAsync(plate, ct))?.TareKg;
}
