namespace Inventory.Domain.Orders;

/// <summary>What the customer asked for: a product and a quantity.</summary>
public sealed record OrderLine(Guid ProductId, int Quantity);
