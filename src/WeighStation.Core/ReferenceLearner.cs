using WeighStation.Core.Abstractions;
using WeighStation.Core.Models;

namespace WeighStation.Core;

/// <summary>
/// Remembers what the operator typed: after a weighing the vehicle, company, cargo and driver
/// are added to the reference books (if new), so next time the form fills itself in.
/// Never overwrites data the operator maintained by hand (tare, permitted mass, notes).
/// </summary>
public sealed class ReferenceLearner(
    IVehicleStore vehicles,
    IReferenceStore<Company> companies,
    IReferenceStore<CargoType> cargos,
    IReferenceStore<Driver> drivers)
{
    public async Task LearnAsync(Weighing w, CancellationToken ct = default)
    {
        var plate = PlateNumber.Normalize(w.Plate);
        if (plate.Length > 0)
        {
            var existing = await vehicles.FindByPlateAsync(plate, ct);
            var updated = (existing ?? new Vehicle { Plate = plate }) with
            {
                Trailer = w.Trailer ?? existing?.Trailer,
                Driver = w.Driver ?? existing?.Driver,
                Company = w.Counterparty ?? existing?.Company,
                Cargo = w.Cargo ?? existing?.Cargo,
            };
            if (existing != updated)
            {
                await vehicles.SaveAsync(updated, ct);
            }
        }

        await AddIfMissingAsync(companies, w.Counterparty, c => c.Name, name => new Company { Name = name }, ct);
        await AddIfMissingAsync(cargos, w.Cargo, c => c.Name, name => new CargoType { Name = name }, ct);
        await AddIfMissingAsync(drivers, w.Driver, d => d.FullName, name => new Driver { FullName = name }, ct);
    }

    private static async Task AddIfMissingAsync<T>(
        IReferenceStore<T> store, string? name, Func<T, string> nameOf, Func<string, T> create, CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();
        if ((await store.GetAllAsync(ct)).Any(x => string.Equals(nameOf(x), name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return;
        }

        try
        {
            await store.SaveAsync(create(name), ct);
        }
        catch (DuplicateException)
        {
            // Added concurrently; nothing to do.
        }
    }
}
