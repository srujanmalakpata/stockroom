using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Abstractions;
using Inventory.Application.Contracts;
using Inventory.Domain.Products;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Api.IntegrationTests;

public sealed class ConcurrencyTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    private readonly HttpClient _client = factory.CreateWriterClient();

    [Fact]
    public async Task TwoSimultaneousReservations_CannotOversell()
    {
        var product = await _client.CreateProductAsync("CONC-TWO");
        await _client.ReceiveAsync(product.Id, "A-01", 10);

        var responses = await RunSimultaneously(2, i => _client.PlaceOrderAsync($"CUST-{i}", (product.Id, 7)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var loser = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("insufficient_stock", (await ProblemJson.ReadAsync(loser)).Code);

        var after = await _client.GetProductAsync(product.Id);
        Assert.Equal((10, 7, 3), (after.OnHand, after.Reserved, after.Available));
    }

    [Fact]
    public async Task ManySimultaneousReservations_ReserveExactlyWhatExists()
    {
        var product = await _client.CreateProductAsync("CONC-MANY");
        await _client.ReceiveAsync(product.Id, "A-01", 6);
        await _client.ReceiveAsync(product.Id, "B-01", 4);

        // 12 requests x 3 units = 36 units demanded against 10 on hand.
        var responses = await RunSimultaneously(12, i => _client.PlaceOrderAsync($"CUST-{i}", (product.Id, 3)));

        // Retry budget: a version check only fails when another order *commits* in between, and only 3
        // orders can ever commit here, so no request needs more than 4 of the factory's 10 attempts.
        // Every rejection must therefore be a clean "insufficient_stock", never an exhausted retry loop.
        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("insufficient_stock", (await ProblemJson.ReadAsync(rejected)).Code);
        }

        var after = await _client.GetProductAsync(product.Id);
        Assert.Equal(10, after.OnHand);
        Assert.Equal(created * 3, after.Reserved); // every success is fully backed by stock
        Assert.InRange(after.Reserved, 0, 10); // nothing is oversold
        Assert.Equal(3, created); // with retries, the 3 orders that fit all succeed (9 of 10 units)
    }

    [Fact]
    public async Task StaleStockRow_IsRejectedByConcurrencyToken()
    {
        var product = await _client.CreateProductAsync("CONC-TOKEN");
        await _client.ReceiveAsync(product.Id, "A-01", 10);

        // Independent units of work load the same stock version.
        await using var scopeA = factory.Services.CreateAsyncScope();
        await using var scopeB = factory.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var rowA = await dbA.StockItems.SingleAsync(s => s.ProductId == product.Id);
        var rowB = await dbB.StockItems.SingleAsync(s => s.ProductId == product.Id);

        // Both reserve 7 against their snapshot of 10 available units.
        rowA.Reserve(7);
        rowB.Reserve(7);

        // The second commit must reject the stale version and translate EF's exception into the
        // application conflict handled by the retry loop.
        await dbA.SaveChangesAsync();
        var unitOfWorkB = scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => unitOfWorkB.SaveChangesAsync(default));
        Assert.IsType<DbUpdateConcurrencyException>(conflict.InnerException);
        Assert.Equal(7, (await _client.GetProductAsync(product.Id)).Reserved);
    }

    [Fact]
    public async Task UniqueIndexViolation_IsTranslatedToConflict()
    {
        // Bypasses the service's "SKU exists?" pre-check, as a racing request would.
        await _client.CreateProductAsync("CONC-UNIQUE");
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Products
            .Add(Product.Create(Guid.NewGuid(), "CONC-UNIQUE", "Racing duplicate", 0, DateTimeOffset.UtcNow));

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(default));
        Assert.Equal("duplicate_sku", ex.Code);
    }

    [Theory]
    [InlineData(10, 2)] // each count alone leaves 22 available (not low), together 4: a stale total misses the alert
    [InlineData(30, 5)] // each count alone leaves 25 available (low), together 10: both would raise one
    public async Task ConcurrentCountsAtTwoLocations_BothSucceed_WithExactlyOneOpenAlert(int threshold, int counted)
    {
        // Regression for write skew: each count reads *both* locations to decide on the alert but writes
        // only its own row, so row tokens alone do not stop both deciding from a stale total. The
        // product-level token (Product.Version) makes the second commit conflict and re-decide.
        for (var round = 0; round < 10; round++)
        {
            var product = await _client.CreateProductAsync($"CONC-SKEW-{threshold}-{round}", threshold);
            await _client.ReceiveAsync(product.Id, "A-01", 20);
            await _client.ReceiveAsync(product.Id, "B-01", 20);

            var responses = await RunSimultaneously(2, i => _client.PostAsJsonAsync(
                $"/api/products/{product.Id}/stock/counts", new RecordCountRequest(i == 0 ? "A-01" : "B-01", counted)));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Equal(2 * counted, (await _client.GetProductAsync(product.Id)).Available);
            var open = await _client.GetFromJsonAsync<List<LowStockAlertResponse>>("/api/alerts/low-stock?limit=100");
            Assert.Single(open!, a => a.ProductId == product.Id);
        }
    }

    [Fact]
    public async Task ConcurrentFirstReceiptsIntoTheSameNewLocation_AreAllApplied()
    {
        // Both requests see no row for N-01 and insert one; the unique index rejects the second insert,
        // which is retried, finds the new row and adds to it instead of failing with 409.
        var product = await _client.CreateProductAsync("CONC-NEWBIN");

        var responses = await RunSimultaneously(5, _ => _client.PostAsJsonAsync(
            $"/api/products/{product.Id}/stock/receipts", new ReceiveStockRequest("N-01", 2)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var after = await _client.GetProductAsync(product.Id);
        Assert.Equal(10, after.OnHand);
        Assert.Single(after.Locations);
    }

    [Fact]
    public async Task ConcurrentFulfilAndCancelOfOneOrder_ExactlyOneWins()
    {
        // Both requests load the same Reserved order; the order's and the stock row's tokens let only one
        // transition commit. The loser retries, sees a closed order and gets 409 invalid_order_state.
        for (var round = 0; round < 10; round++)
        {
            var product = await _client.CreateProductAsync($"CONC-CLOSE-{round}");
            await _client.ReceiveAsync(product.Id, "A-01", 10);
            var placed = await _client.PlaceOrderAsync($"CUST-CLOSE-{round}", (product.Id, 4));
            var order = (await placed.Content.ReadFromJsonAsync<OrderResponse>())!;

            var responses = await RunSimultaneously(2, i => _client.PostAsync(
                $"/api/orders/{order.Id}/{(i == 0 ? "fulfil" : "cancel")}", null));

            var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            var loser = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("invalid_order_state", (await ProblemJson.ReadAsync(loser)).Code);

            var final = (await winner.Content.ReadFromJsonAsync<OrderResponse>())!;
            var after = await _client.GetProductAsync(product.Id);
            Assert.Equal(0, after.Reserved);
            Assert.Equal(final.Status == "Fulfilled" ? 6 : 10, after.OnHand);
            Assert.Equal(final.Status, (await _client.GetFromJsonAsync<OrderResponse>($"/api/orders/{order.Id}"))!.Status);
        }
    }

    [Fact]
    public async Task ExhaustedRetryBudget_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        // Deterministic: a unit of work that always reports a conflict, with a budget of 3 attempts.
        var product = await _client.CreateProductAsync("CONC-EXHAUST");
        var attempts = new AlwaysConflictingUnitOfWork();
        await using var conflicting = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Concurrency:MaxAttempts", "3");
            builder.ConfigureTestServices(services => services.AddScoped<IUnitOfWork>(_ => attempts));
        });
        var client = conflicting.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", InventoryApiFactory.ApiKey);

        var response = await client.PostAsJsonAsync($"/api/products/{product.Id}/stock/receipts", new ReceiveStockRequest("A-01", 5));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemJson.ReadAsync(response);
        Assert.Equal((409, "concurrency_conflict"), (problem.Status, problem.Code));
        Assert.True(problem.Raw.TryGetProperty("traceId", out _));
        Assert.Equal((3, 2), (attempts.Saves, attempts.Discards)); // first try + 2 retries, state discarded before each retry
        Assert.Empty((await _client.GetProductAsync(product.Id)).Locations);
    }

    private sealed class AlwaysConflictingUnitOfWork : IUnitOfWork
    {
        private int _saves;
        private int _discards;

        public int Saves => _saves;

        public int Discards => _discards;

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _saves);
            throw new ConcurrencyConflictException("Simulated conflict.");
        }

        public void DiscardChanges() => Interlocked.Increment(ref _discards);
    }

    private static async Task<HttpResponseMessage[]> RunSimultaneously(int count, Func<int, Task<HttpResponseMessage>> request)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await start.Task;
                return await request(i);
            }))
            .ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }
}
