using Inventory.Application.Abstractions;
using Inventory.Application.Contracts;
using Inventory.Domain.Common;
using Inventory.Domain.Orders;
using Inventory.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Services;

public sealed partial class OrderService(
    IProductRepository products,
    IStockRepository stock,
    IOrderRepository orders,
    IUnitOfWork unitOfWork,
    LowStockMonitor lowStock,
    ConcurrencyRetry retry,
    TimeProvider clock,
    ILogger<OrderService> logger)
{
    /// <summary>Places an order, reserving stock for every line, or fails without reserving anything.</summary>
    public Task<OrderResponse> PlaceAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        // PlaceOrderRequestValidator rejects these first; this keeps a skipped validator from turning a
        // null line into a NullReferenceException (500) instead of a 400.
        if (request.Lines is null || request.Lines.Any(l => l is null))
        {
            throw new DomainException(DomainErrorKind.InvalidInput, "invalid_order_line", "Order lines must be a list of non-null lines.");
        }

        return retry.ExecuteAsync(
            nameof(PlaceAsync),
            async token =>
            {
                var productIds = request.Lines.Select(l => l.ProductId).Distinct().ToList();
                var known = await products.ListByIdsAsync(productIds, token);
                var missing = productIds.Except(known.Select(p => p.Id)).ToList();
                if (missing.Count > 0)
                {
                    throw new NotFoundException("Product", missing[0]);
                }

                var levels = await stock.ListForProductsAsync(productIds, token);
                var order = Order.Place(
                    Guid.NewGuid(),
                    request.CustomerReference,
                    request.Lines.Select(l => new OrderLine(l.ProductId, l.Quantity)),
                    levels,
                    clock.GetUtcNow());

                orders.Add(order);
                await lowStock.EvaluateAsync(known, levels, token);
                await unitOfWork.SaveChangesAsync(token);
                LogPlaced(logger, order.Id, order.CustomerReference, order.Reservations.Count);
                return OrderResponse.From(order);
            },
            ct);
    }

    public async Task<OrderResponse> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct) ?? throw new NotFoundException("Order", id);
        return OrderResponse.From(order);
    }

    public Task<OrderResponse> FulfilAsync(Guid id, CancellationToken ct) =>
        CloseAsync(nameof(FulfilAsync), id, (order, stockById, now) => order.Fulfil(stockById, now), ct);

    public Task<OrderResponse> CancelAsync(Guid id, CancellationToken ct) =>
        CloseAsync(nameof(CancelAsync), id, (order, stockById, now) => order.Cancel(stockById, now), ct);

    private Task<OrderResponse> CloseAsync(
        string operation,
        Guid id,
        Action<Order, IReadOnlyDictionary<Guid, StockItem>, DateTimeOffset> transition,
        CancellationToken ct) =>
        retry.ExecuteAsync(
            operation,
            async token =>
            {
                // Read order: stock first, then the order. A concurrent fulfil/cancel commits the order and its
                // stock rows in one transaction, so if the stock read already sees that commit, the later
                // order read sees it too (409 invalid_order_state). Reading the order first could pair a
                // still-open order with already-released stock, and the domain would then reject the
                // stale mix with a misleading reservation_mismatch before the version check could retry.
                var productIds = await orders.ListProductIdsAsync(id, token);
                var levels = await stock.ListForProductsAsync(productIds, token);
                var order = await orders.GetAsync(id, token) ?? throw new NotFoundException("Order", id);

                transition(order, levels.ToDictionary(s => s.Id), clock.GetUtcNow());

                await lowStock.EvaluateAsync(await products.ListByIdsAsync(productIds, token), levels, token);
                await unitOfWork.SaveChangesAsync(token);
                LogClosed(logger, order.Id, order.Status);
                return OrderResponse.From(order);
            },
            ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Placed order {OrderId} for {CustomerReference} with {ReservationCount} reservation(s)")]
    private static partial void LogPlaced(ILogger logger, Guid orderId, string customerReference, int reservationCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} is now {Status}")]
    private static partial void LogClosed(ILogger logger, Guid orderId, OrderStatus status);
}
