using Inventory.Domain.Alerts;
using Inventory.Domain.Orders;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;

namespace Inventory.Application.Abstractions;

// Persistence ports. The Application layer depends on these interfaces; Infrastructure implements them
// with EF Core. Entities returned by Get/List methods are change-tracked: mutate them and call
// IUnitOfWork.SaveChangesAsync to persist.

public interface IProductRepository
{
    Task<Product?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Product>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<Product>> ListAsync(int offset, int limit, CancellationToken ct);

    Task<bool> SkuExistsAsync(string sku, CancellationToken ct);

    void Add(Product product);
}

public interface IStockRepository
{
    Task<IReadOnlyList<StockItem>> ListForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    Task<IReadOnlyList<StockItem>> ListAtLocationAsync(string? locationCode, int offset, int limit, CancellationToken ct);

    void Add(StockItem item);
}

public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>The products an order is for (untracked; empty if the order does not exist). Never changes after placement.</summary>
    Task<IReadOnlyList<Guid>> ListProductIdsAsync(Guid id, CancellationToken ct);

    void Add(Order order);
}

public interface IAlertRepository
{
    Task<IReadOnlyList<LowStockAlert>> ListOpenForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    Task<IReadOnlyList<LowStockAlert>> ListAsync(bool includeResolved, int offset, int limit, CancellationToken ct);

    void Add(LowStockAlert alert);
}

public interface IUnitOfWork
{
    /// <summary>Commits all tracked changes atomically.</summary>
    /// <exception cref="ConcurrencyConflictException">A row changed since it was read.</exception>
    /// <exception cref="ConflictException">A uniqueness rule was violated.</exception>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Forgets all tracked entities so a retried operation re-reads fresh rows.</summary>
    void DiscardChanges();
}
