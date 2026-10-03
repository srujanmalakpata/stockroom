using Inventory.Domain.Common;

namespace Inventory.Domain.Stock;

/// <summary>
/// The quantity of one product held at one warehouse location.
/// <para>
/// Invariants (enforced by every mutator): <c>OnHand &gt;= 0</c>, <c>Reserved &gt;= 0</c> and
/// <c>Reserved &lt;= OnHand</c>, therefore <c>Available = OnHand - Reserved</c> is never negative.
/// </para>
/// <para>
/// Every mutation increments <see cref="Version"/>, which the persistence layer uses as an optimistic
/// concurrency token: two requests that read the same version cannot both write.
/// </para>
/// </summary>
public sealed class StockItem
{
    /// <summary>
    /// Most units one location can hold. Keeps every per-location figure, and the sum of a product's
    /// locations computed as <see cref="long"/>, far away from <see cref="int"/> overflow.
    /// </summary>
    public const int MaxQuantityPerLocation = 100_000_000;

    private StockItem()
    {
        LocationCode = string.Empty;
    }

    private StockItem(Guid id, Guid productId, string locationCode)
    {
        Id = id;
        ProductId = productId;
        LocationCode = locationCode;
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; private set; }

    public string LocationCode { get; private set; }

    /// <summary>Physical units on the shelf, including units already promised to orders.</summary>
    public int OnHand { get; private set; }

    /// <summary>Units promised to open orders but not yet shipped.</summary>
    public int Reserved { get; private set; }

    public int Available => OnHand - Reserved;

    /// <summary>Optimistic concurrency token, bumped on every change.</summary>
    public int Version { get; private set; }

    public static StockItem Open(Guid id, Guid productId, string locationCode) =>
        new(id, productId, Stock.LocationCode.Normalize(locationCode));

    /// <summary>Goods arrived (purchase receipt, return to stock).</summary>
    public void Receive(int quantity)
    {
        Guard.Positive(quantity, "Received quantity");
        if ((long)OnHand + quantity > MaxQuantityPerLocation)
        {
            throw new DomainException(
                DomainErrorKind.RuleViolation,
                "location_capacity_exceeded",
                $"Receiving {quantity} unit(s) at {LocationCode} would exceed {MaxQuantityPerLocation} units on hand (currently {OnHand}).");
        }

        OnHand += quantity;
        Touch();
    }

    /// <summary>Promise units to an order. Fails without side effects if not enough are available.</summary>
    public void Reserve(int quantity)
    {
        Guard.Positive(quantity, "Reserved quantity");
        if (quantity > Available)
        {
            throw new InsufficientStockException(ProductId, quantity, Available);
        }

        Reserved += quantity;
        Touch();
    }

    /// <summary>Give reserved units back to the available pool (order cancelled).</summary>
    public void ReleaseReservation(int quantity)
    {
        Guard.Positive(quantity, "Released quantity");
        if (quantity > Reserved)
        {
            throw new DomainException(
                DomainErrorKind.RuleViolation,
                "reservation_mismatch",
                $"Cannot release {quantity} unit(s) at {LocationCode}; only {Reserved} reserved.");
        }

        Reserved -= quantity;
        Touch();
    }

    /// <summary>Reserved units physically leave the warehouse (order fulfilled).</summary>
    public void ShipReserved(int quantity)
    {
        Guard.Positive(quantity, "Shipped quantity");
        if (quantity > Reserved)
        {
            throw new DomainException(
                DomainErrorKind.RuleViolation,
                "reservation_mismatch",
                $"Cannot ship {quantity} unit(s) at {LocationCode}; only {Reserved} reserved.");
        }

        Reserved -= quantity;
        OnHand -= quantity;
        Touch();
    }

    /// <summary>
    /// Cycle count: replace the on-hand figure with a physical count. A count below the reserved
    /// quantity is rejected because it would strand open orders; cancel or re-route them first.
    /// </summary>
    public void RecordCount(int countedQuantity)
    {
        Guard.NotNegative(countedQuantity, "Counted quantity");
        if (countedQuantity > MaxQuantityPerLocation)
        {
            throw new DomainException(
                DomainErrorKind.InvalidInput,
                "invalid_quantity",
                $"Counted quantity must be at most {MaxQuantityPerLocation} (was {countedQuantity}).");
        }

        if (countedQuantity < Reserved)
        {
            throw new DomainException(
                DomainErrorKind.RuleViolation,
                "count_below_reserved",
                $"Counted {countedQuantity} unit(s) at {LocationCode} but {Reserved} are reserved for open orders.");
        }

        OnHand = countedQuantity;
        Touch();
    }

    private void Touch() => Version++;
}
