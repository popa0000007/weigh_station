using WeighStation.Core.Models;

namespace WeighStation.Core.Abstractions;

public interface IWeighingRepository
{
    /// <summary>Stores the weighing and returns it with the generated <see cref="Weighing.Id"/>.</summary>
    Task<Weighing> AddAsync(Weighing weighing, CancellationToken ct = default);
    Task<IReadOnlyList<Weighing>> GetRecentAsync(int count, CancellationToken ct = default);
}

public interface ITareProvider
{
    /// <summary>Last known tare of the vehicle, or null if the plate is not in the reference book.</summary>
    Task<int?> FindTareKgAsync(string plate, CancellationToken ct = default);
}
