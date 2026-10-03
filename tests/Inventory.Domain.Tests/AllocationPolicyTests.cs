using Inventory.Domain.Common;
using Inventory.Domain.Orders;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Tests;

public class AllocationPolicyTests
{
    private static readonly Guid ProductId = Guid.NewGuid();

    private static StockItem Stock(string location, int onHand, Guid? productId = null)
    {
        var item = StockItem.Open(Guid.NewGuid(), productId ?? ProductId, location);
        item.Receive(onHand);
        return item;
    }

    [Fact]
    public void Allocate_FromSingleLocationWhenItHasEnough()
    {
        var a = Stock("A", 3);
        var b = Stock("B", 20);

        var plan = AllocationPolicy.Allocate(ProductId, 5, [a, b]);

        var only = Assert.Single(plan);
        Assert.Same(b, only.Stock);
        Assert.Equal(5, only.Quantity);
    }

    [Fact]
    public void Allocate_SplitsLargestAvailableFirst()
    {
        var a = Stock("A", 3);
        var b = Stock("B", 5);
        var c = Stock("C", 4);

        var plan = AllocationPolicy.Allocate(ProductId, 10, [a, b, c]);

        Assert.Equal([("B", 5), ("C", 4), ("A", 1)], plan.Select(p => (p.Stock.LocationCode, p.Quantity)));
    }

    [Fact]
    public void Allocate_BreaksTiesByLocationCode()
    {
        var plan = AllocationPolicy.Allocate(ProductId, 2, [Stock("B", 5), Stock("A", 5)]);

        Assert.Equal("A", Assert.Single(plan).Stock.LocationCode);
    }

    [Fact]
    public void Allocate_IgnoresReservedUnitsAndOtherProducts()
    {
        var reservedOut = Stock("A", 5);
        reservedOut.Reserve(5);
        var otherProduct = Stock("B", 100, Guid.NewGuid());
        var ours = Stock("C", 2);

        var ex = Assert.Throws<InsufficientStockException>(
            () => AllocationPolicy.Allocate(ProductId, 3, [reservedOut, otherProduct, ours]));

        Assert.Equal(2, ex.Available);
        Assert.Equal(3, ex.Requested);
    }

    [Fact]
    public void Allocate_DoesNotMutateStock()
    {
        var a = Stock("A", 5);

        AllocationPolicy.Allocate(ProductId, 5, [a]);

        Assert.Equal(0, a.Reserved);
        Assert.Equal(1, a.Version);
    }

    [Fact]
    public void Allocate_TotalAvailableAboveIntRange_DoesNotOverflow()
    {
        // 30 full locations hold 3 billion units in total, more than int.MaxValue.
        var stock = Enumerable.Range(0, 30).Select(i => Stock($"Z-{i:D2}", StockItem.MaxQuantityPerLocation)).ToList();

        var plan = AllocationPolicy.Allocate(ProductId, StockItem.MaxQuantityPerLocation + 5, stock);

        Assert.Equal(2, plan.Count);
        Assert.Equal(StockItem.MaxQuantityPerLocation + 5, plan.Sum(p => p.Quantity));
    }
}
