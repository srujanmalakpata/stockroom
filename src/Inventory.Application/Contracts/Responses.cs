using Inventory.Domain.Alerts;
using Inventory.Domain.Orders;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;

namespace Inventory.Application.Contracts;

public sealed record StockLevelResponse(
    Guid StockItemId,
    Guid ProductId,
    string LocationCode,
    int OnHand,
    int Reserved,
    int Available)
{
    public static StockLevelResponse From(StockItem s) =>
        new(s.Id, s.ProductId, s.LocationCode, s.OnHand, s.Reserved, s.Available);
}

public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    int ReorderThreshold,
    long OnHand,
    long Reserved,
    long Available,
    bool IsLowStock,
    IReadOnlyList<StockLevelResponse> Locations)
{
    public static ProductResponse From(Product product, IEnumerable<StockItem> stock)
    {
        var locations = stock
            .Where(s => s.ProductId == product.Id)
            .OrderBy(s => s.LocationCode, StringComparer.Ordinal)
            .Select(StockLevelResponse.From)
            .ToList();
        // Totals are long: each location is capped (StockItem.MaxQuantityPerLocation) but many locations
        // together can exceed int.MaxValue, and LINQ's int Sum would throw OverflowException.
        var available = locations.Sum(l => (long)l.Available);
        return new ProductResponse(
            product.Id,
            product.Sku,
            product.Name,
            product.ReorderThreshold,
            locations.Sum(l => (long)l.OnHand),
            locations.Sum(l => (long)l.Reserved),
            available,
            LowStockPolicy.IsLow(available, product.ReorderThreshold),
            locations);
    }
}

public sealed record OrderLineResponse(Guid ProductId, int Quantity);

public sealed record ReservationResponse(Guid StockItemId, Guid ProductId, string LocationCode, int Quantity);

public sealed record OrderResponse(
    Guid Id,
    string CustomerReference,
    string Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<OrderLineResponse> Lines,
    IReadOnlyList<ReservationResponse> Reservations)
{
    public static OrderResponse From(Order order) =>
        new(
            order.Id,
            order.CustomerReference,
            order.Status.ToString(),
            order.PlacedAt,
            order.ClosedAt,
            order.Lines.Select(l => new OrderLineResponse(l.ProductId, l.Quantity)).ToList(),
            order.Reservations
                .Select(r => new ReservationResponse(r.StockItemId, r.ProductId, r.LocationCode, r.Quantity))
                .ToList());
}

public sealed record LowStockAlertResponse(
    Guid Id,
    Guid ProductId,
    string Sku,
    int AvailableAtRaise,
    int ThresholdAtRaise,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt)
{
    public static LowStockAlertResponse From(LowStockAlert a) =>
        new(a.Id, a.ProductId, a.Sku, a.AvailableAtRaise, a.Threshold, a.RaisedAt, a.ResolvedAt);
}
