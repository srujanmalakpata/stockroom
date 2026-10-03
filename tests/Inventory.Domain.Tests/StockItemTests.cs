using Inventory.Domain.Common;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Tests;

public class StockItemTests
{
    private static StockItem NewItem(int onHand = 0)
    {
        var item = StockItem.Open(Guid.NewGuid(), Guid.NewGuid(), "a-01-01");
        if (onHand > 0)
        {
            item.Receive(onHand);
        }

        return item;
    }

    [Fact]
    public void Open_NormalizesLocationCode()
    {
        Assert.Equal("A-01-01", NewItem().LocationCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-A")]
    [InlineData("A B")]
    [InlineData("THIS-LOCATION-CODE-IS-FAR-TOO-LONG")]
    public void Open_RejectsInvalidLocationCode(string code)
    {
        var ex = Assert.Throws<DomainException>(() => StockItem.Open(Guid.NewGuid(), Guid.NewGuid(), code));
        Assert.Equal("invalid_location", ex.Code);
    }

    [Fact]
    public void Receive_IncreasesOnHandAndAvailable()
    {
        var item = NewItem(10);
        item.Receive(5);

        Assert.Equal(15, item.OnHand);
        Assert.Equal(0, item.Reserved);
        Assert.Equal(15, item.Available);
    }

    [Fact]
    public void Reserve_MovesUnitsFromAvailableToReserved()
    {
        var item = NewItem(10);
        item.Reserve(4);

        Assert.Equal(10, item.OnHand);
        Assert.Equal(4, item.Reserved);
        Assert.Equal(6, item.Available);
    }

    [Fact]
    public void Reserve_MoreThanAvailable_ThrowsAndLeavesStockUnchanged()
    {
        var item = NewItem(10);
        item.Reserve(8);
        var versionBefore = item.Version;

        var ex = Assert.Throws<InsufficientStockException>(() => item.Reserve(3));

        Assert.Equal(3, ex.Requested);
        Assert.Equal(2, ex.Available);
        Assert.Equal(8, item.Reserved);
        Assert.Equal(versionBefore, item.Version);
    }

    [Fact]
    public void ShipReserved_ReducesOnHandAndReservedTogether()
    {
        var item = NewItem(10);
        item.Reserve(4);
        item.ShipReserved(4);

        Assert.Equal(6, item.OnHand);
        Assert.Equal(0, item.Reserved);
        Assert.Equal(6, item.Available);
    }

    [Fact]
    public void ReleaseReservation_ReturnsUnitsToAvailable()
    {
        var item = NewItem(10);
        item.Reserve(4);
        item.ReleaseReservation(4);

        Assert.Equal(10, item.Available);
    }

    [Fact]
    public void ShipOrRelease_MoreThanReserved_IsRejected()
    {
        var item = NewItem(10);
        item.Reserve(2);

        Assert.Equal("reservation_mismatch", Assert.Throws<DomainException>(() => item.ShipReserved(3)).Code);
        Assert.Equal("reservation_mismatch", Assert.Throws<DomainException>(() => item.ReleaseReservation(3)).Code);
        Assert.Equal(10, item.OnHand);
        Assert.Equal(2, item.Reserved);
    }

    [Fact]
    public void RecordCount_BelowReserved_IsRejectedSoStockNeverGoesNegative()
    {
        var item = NewItem(10);
        item.Reserve(6);

        var ex = Assert.Throws<DomainException>(() => item.RecordCount(5));

        Assert.Equal("count_below_reserved", ex.Code);
        Assert.Equal(10, item.OnHand);
    }

    [Fact]
    public void RecordCount_SetsOnHand()
    {
        var item = NewItem(10);
        item.Reserve(3);
        item.RecordCount(7);

        Assert.Equal(7, item.OnHand);
        Assert.Equal(4, item.Available);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Mutators_RejectNonPositiveQuantities(int quantity)
    {
        var item = NewItem(10);

        Assert.Throws<DomainException>(() => item.Receive(quantity));
        Assert.Throws<DomainException>(() => item.Reserve(quantity));
        Assert.Throws<DomainException>(() => item.ReleaseReservation(quantity));
        Assert.Throws<DomainException>(() => item.ShipReserved(quantity));
    }

    [Fact]
    public void EverySuccessfulMutation_BumpsVersion()
    {
        var item = NewItem();
        item.Receive(10);
        item.Reserve(5);
        item.ReleaseReservation(1);
        item.ShipReserved(1);
        item.RecordCount(9);

        Assert.Equal(5, item.Version);
    }

    [Fact]
    public void Receive_BeyondLocationCapacity_IsRejectedWithoutOverflow()
    {
        var item = NewItem(StockItem.MaxQuantityPerLocation - 1);

        var ex = Assert.Throws<DomainException>(() => item.Receive(int.MaxValue));

        Assert.Equal("location_capacity_exceeded", ex.Code);
        Assert.Equal(StockItem.MaxQuantityPerLocation - 1, item.OnHand); // unchanged, no wrap-around
        item.Receive(1); // exactly at capacity is fine
        Assert.Equal(StockItem.MaxQuantityPerLocation, item.OnHand);
    }

    [Theory]
    [InlineData(StockItem.MaxQuantityPerLocation + 1)]
    [InlineData(int.MaxValue)]
    public void RecordCount_AboveLocationCapacity_IsRejected(int counted)
    {
        var item = NewItem(5);

        Assert.Equal("invalid_quantity", Assert.Throws<DomainException>(() => item.RecordCount(counted)).Code);
        Assert.Equal(5, item.OnHand);
    }
}
