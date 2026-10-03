using Inventory.Application.Abstractions;
using Inventory.Application.Contracts;
using Inventory.Domain.Products;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Services;

public sealed partial class ProductService(
    IProductRepository products,
    IStockRepository stock,
    IUnitOfWork unitOfWork,
    LowStockMonitor lowStock,
    ConcurrencyRetry retry,
    TimeProvider clock,
    ILogger<ProductService> logger)
{
    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken ct)
    {
        var product = Product.Create(Guid.NewGuid(), request.Sku, request.Name, request.ReorderThreshold, clock.GetUtcNow());
        if (await products.SkuExistsAsync(product.Sku, ct))
        {
            throw new ConflictException("duplicate_sku", $"A product with SKU '{product.Sku}' already exists.");
        }

        products.Add(product);
        // A new product has no stock, so a threshold above zero makes it low from the start; raise the
        // alert now so IsLowStock and the alert list agree.
        await lowStock.EvaluateAsync([product], [], ct);
        await unitOfWork.SaveChangesAsync(ct);
        LogCreated(logger, product.Sku, product.Id);
        return ProductResponse.From(product, []);
    }

    public async Task<ProductResponse> GetAsync(Guid id, CancellationToken ct)
    {
        var product = await products.GetAsync(id, ct) ?? throw new NotFoundException("Product", id);
        var levels = await stock.ListForProductsAsync([id], ct);
        return ProductResponse.From(product, levels);
    }

    public async Task<IReadOnlyList<ProductResponse>> ListAsync(int offset, int limit, CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);
        var page = await products.ListAsync(skip, take, ct);
        var levels = await stock.ListForProductsAsync(page.Select(p => p.Id).ToList(), ct);
        return page.Select(p => ProductResponse.From(p, levels)).ToList();
    }

    public Task<ProductResponse> ChangeReorderThresholdAsync(Guid id, ChangeReorderThresholdRequest request, CancellationToken ct) =>
        retry.ExecuteAsync(
            nameof(ChangeReorderThresholdAsync),
            async token =>
            {
                var product = await products.GetAsync(id, token) ?? throw new NotFoundException("Product", id);
                product.ChangeReorderThreshold(request.ReorderThreshold);
                var levels = await stock.ListForProductsAsync([id], token);
                await lowStock.EvaluateAsync([product], levels, token);
                await unitOfWork.SaveChangesAsync(token);
                return ProductResponse.From(product, levels);
            },
            ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created product {Sku} ({ProductId})")]
    private static partial void LogCreated(ILogger logger, string sku, Guid productId);
}
