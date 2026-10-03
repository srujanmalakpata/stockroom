namespace Inventory.Domain.Orders;

/// <summary>Where the order's units are held: a quantity reserved against one <see cref="Stock.StockItem"/>.</summary>
public sealed record StockReservation(Guid StockItemId, Guid ProductId, string LocationCode, int Quantity);
