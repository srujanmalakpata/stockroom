using Inventory.Domain.Common;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Orders;

/// <summary>
/// A customer order and the stock reserved for it. Lifecycle:
/// <c>Reserved → Fulfilled</c> (stock shipped) or <c>Reserved → Cancelled</c> (stock released).
/// Both transitions are terminal.
/// </summary>
public sealed class Order
{
    public const int MaxCustomerReferenceLength = 64;

    private readonly List<OrderLine> _lines = [];
    private readonly List<StockReservation> _reservations = [];

    private Order()
    {
        CustomerReference = string.Empty;
    }

    private Order(Guid id, string customerReference, DateTimeOffset placedAt)
    {
        Id = id;
        CustomerReference = customerReference;
        PlacedAt = Timestamps.Normalize(placedAt);
        Status = OrderStatus.Reserved;
    }

    public Guid Id { get; private set; }

    public string CustomerReference { get; private set; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public int Version { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public IReadOnlyList<StockReservation> Reservations => _reservations;

    /// <summary>
    /// Places an order and reserves stock for every line. Allocation for all lines is planned before any
    /// reservation is applied, so an order that cannot be filled in full leaves stock untouched.
    /// <paramref name="stock"/> must contain every stock item (any location) for the ordered products.
    /// </summary>
    public static Order Place(
        Guid id,
        string customerReference,
        IEnumerable<OrderLine> requestedLines,
        IReadOnlyCollection<StockItem> stock,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(customerReference) || customerReference.Trim().Length > MaxCustomerReferenceLength)
        {
            throw new DomainException(
                DomainErrorKind.InvalidInput,
                "invalid_customer_reference",
                $"Customer reference must be 1-{MaxCustomerReferenceLength} characters.");
        }

        // Merge duplicate lines for the same product so allocation sees the true total.
        var lines = requestedLines
            .GroupBy(l => l.ProductId)
            .Select(g => new OrderLine(g.Key, MergedQuantity(g)))
            .ToList();
        if (lines.Count == 0)
        {
            throw new DomainException(DomainErrorKind.InvalidInput, "empty_order", "An order needs at least one line.");
        }

        // Plan every line before mutating stock; insufficient stock leaves all reservations untouched.
        var plan = lines
            .SelectMany(line => AllocationPolicy.Allocate(line.ProductId, line.Quantity, stock))
            .ToList();

        // Each product's allocation targets distinct stock items, so applying the plan cannot fail.
        var order = new Order(id, customerReference.Trim(), now);
        order._lines.AddRange(lines);
        foreach (var (item, quantity) in plan)
        {
            item.Reserve(quantity);
            order._reservations.Add(new StockReservation(item.Id, item.ProductId, item.LocationCode, quantity));
        }

        return order;
    }

    /// <summary>Ships the order: reserved units leave on-hand stock.</summary>
    public void Fulfil(IReadOnlyDictionary<Guid, StockItem> stockById, DateTimeOffset now)
    {
        EnsureOpen("fulfil");
        foreach (var reservation in _reservations)
        {
            Lookup(stockById, reservation).ShipReserved(reservation.Quantity);
        }

        Close(OrderStatus.Fulfilled, now);
    }

    /// <summary>Cancels the order: reserved units become available again.</summary>
    public void Cancel(IReadOnlyDictionary<Guid, StockItem> stockById, DateTimeOffset now)
    {
        EnsureOpen("cancel");
        foreach (var reservation in _reservations)
        {
            Lookup(stockById, reservation).ReleaseReservation(reservation.Quantity);
        }

        Close(OrderStatus.Cancelled, now);
    }

    private static int MergedQuantity(IEnumerable<OrderLine> sameProduct)
    {
        // Summed as long so duplicate lines cannot overflow into a negative quantity.
        var total = sameProduct.Sum(l => (long)Guard.Positive(l.Quantity, "Line quantity"));
        return total <= int.MaxValue
            ? (int)total
            : throw new DomainException(DomainErrorKind.InvalidInput, "invalid_quantity", $"Total quantity for one product is too large (was {total}).");
    }

    private void EnsureOpen(string action)
    {
        if (Status != OrderStatus.Reserved)
        {
            throw new DomainException(
                DomainErrorKind.RuleViolation,
                "invalid_order_state",
                $"Cannot {action} order {Id} because it is already {Status.ToString().ToLowerInvariant()}.");
        }
    }

    private void Close(OrderStatus status, DateTimeOffset now)
    {
        Status = status;
        ClosedAt = Timestamps.Normalize(now);
        Version++;
    }

    private static StockItem Lookup(IReadOnlyDictionary<Guid, StockItem> stockById, StockReservation reservation) =>
        stockById.TryGetValue(reservation.StockItemId, out var item)
            ? item
            : throw new InvalidOperationException(
                $"Stock item {reservation.StockItemId} for reservation was not loaded.");
}
