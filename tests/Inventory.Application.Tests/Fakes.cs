using Inventory.Application.Abstractions;
using Inventory.Domain.Alerts;

namespace Inventory.Application.Tests;

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Discards { get; private set; }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public void DiscardChanges() => Discards++;
}

internal sealed class InMemoryAlertRepository : IAlertRepository
{
    public List<LowStockAlert> Alerts { get; } = [];

    public Task<IReadOnlyList<LowStockAlert>> ListOpenForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LowStockAlert>>(Alerts.Where(a => a.IsOpen && productIds.Contains(a.ProductId)).ToList());

    public Task<IReadOnlyList<LowStockAlert>> ListAsync(bool includeResolved, int offset, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LowStockAlert>>(Alerts.Where(a => includeResolved || a.IsOpen).Skip(offset).Take(limit).ToList());

    public void Add(LowStockAlert alert) => Alerts.Add(alert);
}
