using Inventory.Application.Abstractions;
using Inventory.Application.Contracts;
using Inventory.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Services;

public sealed partial class StockService(
    IProductRepository products,
    IStockRepository stock,
    IUnitOfWork unitOfWork,
    LowStockMonitor lowStock,
    ConcurrencyRetry retry,
    ILogger<StockService> logger)
{
    public Task<ProductResponse> ReceiveAsync(Guid productId, ReceiveStockRequest request, CancellationToken ct) =>
        ChangeStockAsync(
            nameof(ReceiveAsync),
            productId,
            request.LocationCode,
            createIfMissing: true,
            item =>
            {
                item.Receive(request.Quantity);
                LogReceived(logger, request.Quantity, item.LocationCode, productId);
            },
            ct);

    public Task<ProductResponse> RecordCountAsync(Guid productId, RecordCountRequest request, CancellationToken ct) =>
        ChangeStockAsync(
            nameof(RecordCountAsync),
            productId,
            request.LocationCode,
            createIfMissing: false, // counts reconcile known bins; a typo must not create an empty one
            item =>
            {
                var before = item.OnHand;
                item.RecordCount(request.CountedQuantity);
                LogCounted(logger, item.LocationCode, productId, before, item.OnHand);
            },
            ct);

    public async Task<IReadOnlyList<StockLevelResponse>> ListAsync(string? locationCode, int offset, int limit, CancellationToken ct)
    {
        var normalized = string.IsNullOrWhiteSpace(locationCode) ? null : LocationCode.Normalize(locationCode);
        var (skip, take) = Paging.Clamp(offset, limit);
        var items = await stock.ListAtLocationAsync(normalized, skip, take, ct);
        return items.Select(StockLevelResponse.From).ToList();
    }

    private Task<ProductResponse> ChangeStockAsync(
        string operation,
        Guid productId,
        string locationCode,
        bool createIfMissing,
        Action<StockItem> change,
        CancellationToken ct) =>
        retry.ExecuteAsync(
            operation,
            async token =>
            {
                var product = await products.GetAsync(productId, token) ?? throw new NotFoundException("Product", productId);
                var location = LocationCode.Normalize(locationCode);
                var levels = (await stock.ListForProductsAsync([productId], token)).ToList();

                var item = levels.FirstOrDefault(s => s.LocationCode == location);
                if (item is null && createIfMissing)
                {
                    item = StockItem.Open(Guid.NewGuid(), productId, location);
                    stock.Add(item);
                    levels.Add(item);
                }

                change(item ?? throw new NotFoundException("Stock location", location));
                await lowStock.EvaluateAsync([product], levels, token);
                await unitOfWork.SaveChangesAsync(token);
                return ProductResponse.From(product, levels);
            },
            ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Received {Quantity} unit(s) at {Location} for product {ProductId}")]
    private static partial void LogReceived(ILogger logger, int quantity, string location, Guid productId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cycle count at {Location} for product {ProductId}: on-hand {Before} -> {After}")]
    private static partial void LogCounted(ILogger logger, string location, Guid productId, int before, int after);
}
