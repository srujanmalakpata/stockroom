using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Contracts;
using Inventory.Domain.Common;

namespace Inventory.Api.IntegrationTests;

public sealed class OrderLifecycleTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    private readonly HttpClient _client = factory.CreateWriterClient();

    [Fact]
    public async Task PlaceFulfilAndCancel_MoveStockThroughReservedToShippedOrReleased()
    {
        var widget = await _client.CreateProductAsync("WIDGET-LC");
        await _client.ReceiveAsync(widget.Id, "A-01", 6);
        await _client.ReceiveAsync(widget.Id, "B-01", 4);

        // Place: 8 units split across both locations, largest first.
        var placed = await _client.PlaceOrderAsync("CUST-100", (widget.Id, 8));
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var order = (await placed.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal("Reserved", order.Status);
        Assert.Equal([("A-01", 6), ("B-01", 2)], order.Reservations.Select(r => (r.LocationCode, r.Quantity)));

        var afterPlace = await _client.GetProductAsync(widget.Id);
        Assert.Equal((10, 8, 2), (afterPlace.OnHand, afterPlace.Reserved, afterPlace.Available));

        // Fulfil: reserved units leave the building.
        var fulfilled = await _client.PostAsync($"/api/orders/{order.Id}/fulfil", null);
        Assert.Equal(HttpStatusCode.OK, fulfilled.StatusCode);
        var afterFulfil = await _client.GetProductAsync(widget.Id);
        Assert.Equal((2, 0, 2), (afterFulfil.OnHand, afterFulfil.Reserved, afterFulfil.Available));

        // Cancel a second order: its reservation is released.
        var second = (await (await _client.PlaceOrderAsync("CUST-101", (widget.Id, 2))).Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal(0, (await _client.GetProductAsync(widget.Id)).Available);
        var cancelled = await _client.PostAsync($"/api/orders/{second.Id}/cancel", null);
        Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<OrderResponse>())!.Status);
        Assert.Equal(2, (await _client.GetProductAsync(widget.Id)).Available);

        var again = await _client.PostAsync($"/api/orders/{second.Id}/fulfil", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_order_state", (await ProblemJson.ReadAsync(again)).Code);
    }

    [Fact]
    public async Task PlaceOrder_InsufficientStock_Returns409AndReservesNothing()
    {
        var bolt = await _client.CreateProductAsync("BOLT-LC");
        var nut = await _client.CreateProductAsync("NUT-LC");
        await _client.ReceiveAsync(bolt.Id, "A-01", 10);
        await _client.ReceiveAsync(nut.Id, "A-01", 1);

        var response = await _client.PlaceOrderAsync("CUST-200", (bolt.Id, 5), (nut.Id, 3));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemJson.ReadAsync(response);
        Assert.Equal("insufficient_stock", problem.Code);
        Assert.Equal(1, problem.Raw.GetProperty("available").GetInt32());
        Assert.Equal(0, (await _client.GetProductAsync(bolt.Id)).Reserved); // all-or-nothing
    }

    [Fact]
    public async Task PlaceOrder_UnknownProduct_Returns404()
    {
        var response = await _client.PlaceOrderAsync("CUST-300", (Guid.NewGuid(), 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlaceOrder_InvalidLines_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/orders", new PlaceOrderRequest("", [new(Guid.Empty, 0)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await ProblemJson.ReadAsync(response)).Raw.GetProperty("errors");
        Assert.True(errors.TryGetProperty("CustomerReference", out _));
        Assert.True(errors.TryGetProperty("Lines[0].Quantity", out _));
    }

    [Theory]
    [InlineData("""{"customerReference":"C-1","lines":[null]}""", "Lines[0]")]
    [InlineData("""{"customerReference":"C-1","lines":null}""", "Lines")]
    public async Task PlaceOrder_NullLines_Returns400NotA500(string body, string errorKey)
    {
        // Regression: FluentValidation's ChildRules skips null elements, so [null] used to reach the
        // service and fail with a NullReferenceException (500).
        var response = await _client.PostAsync("/api/orders", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemJson.ReadAsync(response);
        Assert.Equal("validation_failed", problem.Code);
        Assert.True(problem.Raw.GetProperty("errors").TryGetProperty(errorKey, out _));
    }

    [Fact]
    public async Task Timestamps_AreTheSameInTheWriteResponseAndInLaterReads()
    {
        // Regression: the 201 echoed the in-memory 100 ns value (…00.1234567) while reads returned what
        // the database kept (0.1 ms on SQLite, 1 µs on PostgreSQL). The domain now stores UTC milliseconds.
        var bolt = await _client.CreateProductAsync("BOLT-TIME");
        await _client.ReceiveAsync(bolt.Id, "A-01", 5);
        factory.Clock.Advance(TimeSpan.FromTicks(1_234_567));

        var placed = (await (await _client.PlaceOrderAsync("CUST-TIME", (bolt.Id, 1))).Content.ReadFromJsonAsync<OrderResponse>())!;
        var cancelled = (await (await _client.PostAsync($"/api/orders/{placed.Id}/cancel", null)).Content.ReadFromJsonAsync<OrderResponse>())!;
        var read = (await _client.GetFromJsonAsync<OrderResponse>($"/api/orders/{placed.Id}"))!;

        Assert.Equal(TimeSpan.Zero, placed.PlacedAt.Offset);
        Assert.Equal(0, placed.PlacedAt.UtcTicks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(placed.PlacedAt.UtcTicks, read.PlacedAt.UtcTicks);
        Assert.Equal(cancelled.ClosedAt!.Value.UtcTicks, read.ClosedAt!.Value.UtcTicks);
    }

    [Fact]
    public async Task LowStockAlert_IsRaisedWhenAvailableDropsBelowThresholdAndResolvedOnRestock()
    {
        var gear = await _client.CreateProductAsync("GEAR-LC", reorderThreshold: 5);
        await _client.ReceiveAsync(gear.Id, "A-01", 6); // 6 >= 5: not low
        Assert.DoesNotContain(await OpenAlerts(), a => a.ProductId == gear.Id);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await _client.PlaceOrderAsync("CUST-400", (gear.Id, 3)); // available 3 < 5
        var raised = Assert.Single(await OpenAlerts(), a => a.ProductId == gear.Id);
        Assert.Equal((3, 5), (raised.AvailableAtRaise, raised.ThresholdAtRaise));
        Assert.Equal(Timestamps.Normalize(factory.Clock.GetUtcNow()), raised.RaisedAt);

        await _client.PlaceOrderAsync("CUST-401", (gear.Id, 1)); // still low: no duplicate alert
        Assert.Single(await OpenAlerts(), a => a.ProductId == gear.Id);

        await _client.ReceiveAsync(gear.Id, "A-01", 10); // available 12 >= 5
        Assert.DoesNotContain(await OpenAlerts(), a => a.ProductId == gear.Id);
        var history = await _client.GetFromJsonAsync<List<LowStockAlertResponse>>("/api/alerts/low-stock?includeResolved=true");
        Assert.All(history!.Where(a => a.ProductId == gear.Id), a => Assert.NotNull(a.ResolvedAt));
    }

    private async Task<List<LowStockAlertResponse>> OpenAlerts() =>
        (await _client.GetFromJsonAsync<List<LowStockAlertResponse>>("/api/alerts/low-stock"))!;
}
