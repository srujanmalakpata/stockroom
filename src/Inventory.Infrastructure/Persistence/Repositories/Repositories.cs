using Inventory.Application.Abstractions;
using Inventory.Domain.Alerts;
using Inventory.Domain.Orders;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence.Repositories;

internal sealed class ProductRepository(InventoryDbContext db) : IProductRepository
{
    public Task<Product?> GetAsync(Guid id, CancellationToken ct) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Product>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<Product>> ListAsync(int offset, int limit, CancellationToken ct) =>
        await db.Products.AsNoTracking().OrderBy(p => p.Sku).Skip(offset).Take(limit).ToListAsync(ct);

    public Task<bool> SkuExistsAsync(string sku, CancellationToken ct) =>
        db.Products.AnyAsync(p => p.Sku == sku, ct);

    public void Add(Product product) => db.Products.Add(product);
}

internal sealed class StockRepository(InventoryDbContext db) : IStockRepository
{
    public async Task<IReadOnlyList<StockItem>> ListForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        await db.StockItems.Where(s => productIds.Contains(s.ProductId)).ToListAsync(ct);

    public async Task<IReadOnlyList<StockItem>> ListAtLocationAsync(string? locationCode, int offset, int limit, CancellationToken ct) =>
        await db.StockItems.AsNoTracking()
            .Where(s => locationCode == null || s.LocationCode == locationCode)
            .OrderBy(s => s.LocationCode)
            .ThenBy(s => s.ProductId) // (LocationCode, ProductId) is unique, so pages are stable
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public void Add(StockItem item) => db.StockItems.Add(item);
}

internal sealed class OrderRepository(InventoryDbContext db) : IOrderRepository
{
    public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Guid>> ListProductIdsAsync(Guid id, CancellationToken ct) =>
        await db.Orders.AsNoTracking()
            .Where(o => o.Id == id)
            .SelectMany(o => o.Lines.Select(l => l.ProductId))
            .ToListAsync(ct);

    public void Add(Order order) => db.Orders.Add(order);
}

internal sealed class AlertRepository(InventoryDbContext db) : IAlertRepository
{
    public async Task<IReadOnlyList<LowStockAlert>> ListOpenForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        await db.LowStockAlerts.Where(a => productIds.Contains(a.ProductId) && a.ResolvedAt == null).ToListAsync(ct);

    public async Task<IReadOnlyList<LowStockAlert>> ListAsync(bool includeResolved, int offset, int limit, CancellationToken ct) =>
        await db.LowStockAlerts.AsNoTracking()
            .Where(a => includeResolved || a.ResolvedAt == null)
            .OrderByDescending(a => a.RaisedAt)
            .ThenBy(a => a.Id) // tie-breaker so pages are stable
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public void Add(LowStockAlert alert) => db.LowStockAlerts.Add(alert);
}
