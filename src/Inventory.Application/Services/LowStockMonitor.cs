using Inventory.Application.Abstractions;
using Inventory.Domain.Alerts;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Services;

/// <summary>
/// Applies <see cref="LowStockPolicy"/> after a stock change. It only stages alert changes; the caller's
/// SaveChanges commits them in the same transaction as the stock change that caused them.
/// <para>
/// The decision reads <em>every</em> location of a product, but row-level tokens only protect the rows a
/// request writes. Two requests changing different locations of one product could otherwise both decide
/// from a stale total (write skew), e.g. both raise an alert. So each evaluation also bumps the product's
/// <see cref="Product.Version"/>: the second commit then fails its concurrency check and is retried on
/// fresh data.
/// </para>
/// </summary>
public sealed partial class LowStockMonitor(IAlertRepository alerts, TimeProvider clock, ILogger<LowStockMonitor> logger)
{
    public async Task EvaluateAsync(IReadOnlyCollection<Product> products, IReadOnlyCollection<StockItem> stock, CancellationToken ct)
    {
        var productIds = products.Select(p => p.Id).ToList();
        var openAlerts = (await alerts.ListOpenForProductsAsync(productIds, ct)).ToDictionary(a => a.ProductId);
        var now = clock.GetUtcNow();

        foreach (var product in products)
        {
            product.MarkStockChanged();
            var available = stock.Where(s => s.ProductId == product.Id).Sum(s => (long)s.Available);
            openAlerts.TryGetValue(product.Id, out var open);

            switch (LowStockPolicy.Evaluate(available, product.ReorderThreshold, open is not null))
            {
                case LowStockDecision.Raise:
                    // Raise only happens when available < threshold (an int), so the cast is lossless.
                    alerts.Add(LowStockAlert.Raise(Guid.NewGuid(), product.Id, product.Sku, (int)available, product.ReorderThreshold, now));
                    LogRaised(logger, product.Sku, available, product.ReorderThreshold);
                    break;
                case LowStockDecision.Resolve:
                    open!.Resolve(now);
                    LogResolved(logger, product.Sku, available, product.ReorderThreshold);
                    break;
                case LowStockDecision.NoChange:
                default:
                    break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Low stock: {Sku} has {Available} available, below reorder threshold {Threshold}")]
    private static partial void LogRaised(ILogger logger, string sku, long available, int threshold);

    [LoggerMessage(Level = LogLevel.Information, Message = "Low stock resolved: {Sku} has {Available} available (threshold {Threshold})")]
    private static partial void LogResolved(ILogger logger, string sku, long available, int threshold);
}
