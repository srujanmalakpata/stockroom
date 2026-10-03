namespace Inventory.Domain.Orders;

public enum OrderStatus
{
    /// <summary>Placed and stock is reserved; awaiting picking and shipping.</summary>
    Reserved = 0,

    /// <summary>Shipped: reserved stock has left the warehouse.</summary>
    Fulfilled = 1,

    /// <summary>Cancelled before shipping: reserved stock was released.</summary>
    Cancelled = 2,
}
