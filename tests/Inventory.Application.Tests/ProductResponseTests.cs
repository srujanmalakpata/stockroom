using Inventory.Application.Contracts;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;

namespace Inventory.Application.Tests;

public class ProductResponseTests
{
    [Fact]
    public void Totals_AboveIntRange_AreSummedAsLong()
    {
        // Regression: totals used to be int sums, so many full locations threw OverflowException (HTTP 500)
        // on every read of the product, even after the write that caused it had committed.
        var product = Product.Create(Guid.NewGuid(), "BULK-1", "Bulk", 0, DateTimeOffset.UnixEpoch);
        var stock = Enumerable.Range(0, 30).Select(i =>
        {
            var item = StockItem.Open(Guid.NewGuid(), product.Id, $"Z-{i:D2}");
            item.Receive(StockItem.MaxQuantityPerLocation);
            return item;
        }).ToList();
        stock[0].Reserve(10);

        var response = ProductResponse.From(product, stock);

        Assert.Equal(30L * StockItem.MaxQuantityPerLocation, response.OnHand);
        Assert.Equal(10, response.Reserved);
        Assert.Equal(response.OnHand - 10, response.Available);
        Assert.Equal(30, response.Locations.Count);
    }
}
