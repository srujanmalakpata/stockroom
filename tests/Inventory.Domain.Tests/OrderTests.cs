using Inventory.Domain.Common;
using Inventory.Domain.Orders;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Tests;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _widget = Guid.NewGuid();
    private readonly Guid _gadget = Guid.NewGuid();

    private static StockItem Stock(Guid productId, string location, int onHand)
    {
        var item = StockItem.Open(Guid.NewGuid(), productId, location);
        item.Receive(onHand);
        return item;
    }

    [Fact]
    public void Place_ReservesStockForEveryLine()
    {
        var widgetA = Stock(_widget, "A", 4);
        var widgetB = Stock(_widget, "B", 6);
        var gadget = Stock(_gadget, "A", 10);

        var order = Order.Place(Guid.NewGuid(), "CUST-1", [new(_widget, 8), new(_gadget, 1)], [widgetA, widgetB, gadget], Now);

        Assert.Equal(OrderStatus.Reserved, order.Status);
        Assert.Equal(9, order.Reservations.Sum(r => r.Quantity));
        Assert.Equal(6, widgetB.Reserved); // largest location first
        Assert.Equal(2, widgetA.Reserved); // remainder allocated to the smaller location
        Assert.Equal(1, gadget.Reserved);
    }

    [Fact]
    public void Place_WhenAnyLineIsShort_ReservesNothing()
    {
        var widget = Stock(_widget, "A", 10);
        var gadget = Stock(_gadget, "A", 1);

        var ex = Assert.Throws<InsufficientStockException>(
            () => Order.Place(Guid.NewGuid(), "CUST-1", [new(_widget, 5), new(_gadget, 2)], [widget, gadget], Now));

        Assert.Equal(_gadget, ex.ProductId);
        Assert.Equal(0, widget.Reserved); // the first line was not reserved either
        Assert.Equal(0, gadget.Reserved);
    }

    [Fact]
    public void Place_MergesDuplicateLinesBeforeAllocating()
    {
        var widget = Stock(_widget, "A", 5);

        Assert.Throws<InsufficientStockException>(
            () => Order.Place(Guid.NewGuid(), "CUST-1", [new(_widget, 3), new(_widget, 3)], [widget], Now));

        var order = Order.Place(Guid.NewGuid(), "CUST-1", [new(_widget, 2), new(_widget, 3)], [widget], Now);
        Assert.Equal(5, Assert.Single(order.Lines).Quantity);
        Assert.Equal(5, widget.Reserved);
    }

    [Fact]
    public void Place_RejectsEmptyOrderAndBlankCustomer()
    {
        Assert.Equal("empty_order", Assert.Throws<DomainException>(
            () => Order.Place(Guid.NewGuid(), "CUST-1", [], [], Now)).Code);
        Assert.Equal("invalid_customer_reference", Assert.Throws<DomainException>(
            () => Order.Place(Guid.NewGuid(), "  ", [new(_widget, 1)], [], Now)).Code);
    }

    [Fact]
    public void Fulfil_ShipsReservedStockAndClosesOrder()
    {
        var widget = Stock(_widget, "A", 10);
        var order = Order.Place(Guid.NewGuid(), "CUST-1", [new(_widget, 4)], [widget], Now);

        order.Fulfil(new Dictionary<Guid, StockItem> { [widget.Id] = widget }, Now.AddHours(1));

        Assert.Equal(OrderStatus.Fulfilled, order.Status);
        Assert.Equal(Now.AddHours(1), order.ClosedAt);
        Assert.Equal(6, widget.OnHand);
        Assert.Equal(0, widget.Reserved);
    }

    [Fact]
    public void Cancel_ReleasesReservedStock()
    {
        var widget = Stock(_widget, "A", 10);
        var order = Order.Place(Guid.NewGuid(), "CUST-1", [new(_widget, 4)], [widget], Now);

        order.Cancel(new Dictionary<Guid, StockItem> { [widget.Id] = widget }, Now);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(10, widget.OnHand);
        Assert.Equal(10, widget.Available);
    }

    [Fact]
    public void ClosedOrders_CannotTransitionAgain()
    {
        var widget = Stock(_widget, "A", 10);
        var stock = new Dictionary<Guid, StockItem> { [widget.Id] = widget };
        var order = Order.Place(Guid.NewGuid(), "CUST-1", [new(_widget, 4)], [widget], Now);
        order.Cancel(stock, Now);

        Assert.Equal("invalid_order_state", Assert.Throws<DomainException>(() => order.Cancel(stock, Now)).Code);
        Assert.Equal("invalid_order_state", Assert.Throws<DomainException>(() => order.Fulfil(stock, Now)).Code);
        Assert.Equal(10, widget.Available); // not released twice
    }

    [Fact]
    public void Place_DuplicateLinesThatOverflowInt_AreRejected()
    {
        var stock = new[] { Stock(_widget, "A-01", 10) };

        var ex = Assert.Throws<DomainException>(() => Order.Place(
            Guid.NewGuid(), "CUST-OVF", [new(_widget, int.MaxValue), new(_widget, int.MaxValue)], stock, Now));

        Assert.Equal("invalid_quantity", ex.Code);
        Assert.Equal(0, stock[0].Reserved);
    }
}
