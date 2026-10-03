using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Contracts;
using Inventory.Domain.Stock;

namespace Inventory.Api.IntegrationTests;

public sealed class ProductApiTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    [Fact]
    public async Task WriteEndpoints_RequireValidApiKey()
    {
        var anonymous = factory.CreateClient();
        var wrongKey = factory.CreateClient();
        wrongKey.DefaultRequestHeaders.Add("X-Api-Key", "not-the-key");
        var request = new CreateProductRequest("AUTH-1", "Auth test", 0);

        var noKey = await anonymous.PostAsJsonAsync("/api/products", request);
        var badKey = await wrongKey.PostAsJsonAsync("/api/products", request);

        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, badKey.StatusCode);
        Assert.Equal("unauthorized", (await ProblemJson.ReadAsync(noKey)).Code); // status-code pages get a code too
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/products")).StatusCode); // reads are open
    }

    [Fact]
    public async Task CreateProduct_Returns201WithLocationAndNormalizedSku()
    {
        var client = factory.CreateWriterClient();

        var response = await client.PostAsJsonAsync("/api/products", new CreateProductRequest("bolt-m8", "M8 bolt", 10));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.Equal("BOLT-M8", product!.Sku);
        Assert.Equal($"/api/products/{product.Id}", response.Headers.Location?.AbsolutePath);
        Assert.True(product.IsLowStock); // 0 available < threshold 10
    }

    [Fact]
    public async Task CreateProduct_DuplicateSku_Returns409Problem()
    {
        var client = factory.CreateWriterClient();
        await client.CreateProductAsync("DUP-1");

        var response = await client.PostAsJsonAsync("/api/products", new CreateProductRequest("dup-1", "Again", 0));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("duplicate_sku", (await ProblemJson.ReadAsync(response)).Code);
    }

    [Fact]
    public async Task CreateProduct_InvalidBody_Returns400ValidationProblem()
    {
        var client = factory.CreateWriterClient();

        var response = await client.PostAsJsonAsync("/api/products", new CreateProductRequest("x", "", -1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemJson.ReadAsync(response);
        Assert.Equal("validation_failed", problem.Code);
        Assert.True(problem.Raw.TryGetProperty("traceId", out _));
        var errors = problem.Raw.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Sku", out _));
        Assert.True(errors.TryGetProperty("Name", out _));
        Assert.True(errors.TryGetProperty("ReorderThreshold", out _));
    }

    [Fact]
    public async Task MalformedJson_Returns400Problem()
    {
        var client = factory.CreateWriterClient();

        var response = await client.PostAsync(
            "/api/products", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemJson.ReadAsync(response);
        Assert.Equal(400, problem.Status);
        Assert.Equal("bad_request", problem.Code);
        Assert.True(problem.Raw.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task UnknownRoute_Returns404ProblemWithCode()
    {
        var response = await factory.CreateClient().GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await ProblemJson.ReadAsync(response)).Code);
    }

    [Fact]
    public async Task UnknownProduct_Returns404Problem()
    {
        var response = await factory.CreateClient().GetAsync($"/api/products/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await ProblemJson.ReadAsync(response)).Code);
    }

    [Fact]
    public async Task ReceiveAndCount_UpdateStockPerLocation()
    {
        var client = factory.CreateWriterClient();
        var product = await client.CreateProductAsync("NUT-M8");

        await client.ReceiveAsync(product.Id, "a-01", 30);
        await client.ReceiveAsync(product.Id, "B-02", 5);
        var counted = await client.PostAsJsonAsync($"/api/products/{product.Id}/stock/counts", new RecordCountRequest("A-01", 28));
        counted.EnsureSuccessStatusCode();

        var after = await client.GetProductAsync(product.Id);
        Assert.Equal(33, after.OnHand);
        Assert.Equal(["A-01", "B-02"], after.Locations.Select(l => l.LocationCode));

        var atA01 = await client.GetFromJsonAsync<List<StockLevelResponse>>("/api/stock?location=a-01");
        Assert.Contains(atA01!, s => s.ProductId == product.Id && s.OnHand == 28);
    }

    [Fact]
    public async Task Count_AtUnknownLocation_Returns404AndCreatesNoBin()
    {
        var client = factory.CreateWriterClient();
        var product = await client.CreateProductAsync("NUT-M10");
        await client.ReceiveAsync(product.Id, "A-01", 3);

        var response = await client.PostAsJsonAsync($"/api/products/{product.Id}/stock/counts", new RecordCountRequest("A-10", 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await ProblemJson.ReadAsync(response)).Code);
        Assert.Equal(["A-01"], (await client.GetProductAsync(product.Id)).Locations.Select(l => l.LocationCode));
    }

    [Fact]
    public async Task HugeQuantities_AreRejectedWith4xx_AndReadsKeepWorking()
    {
        // Regression: an unbounded count followed by more stock overflowed the int totals, so the write
        // returned 500 after committing and every later read of the product returned 500.
        var client = factory.CreateWriterClient();
        var product = await client.CreateProductAsync("BULK-API");
        await client.ReceiveAsync(product.Id, "A-01", 1);

        var tooBig = await client.PostAsJsonAsync($"/api/products/{product.Id}/stock/counts", new RecordCountRequest("A-01", int.MaxValue));
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal("validation_failed", (await ProblemJson.ReadAsync(tooBig)).Code);

        var full = await client.PostAsJsonAsync(
            $"/api/products/{product.Id}/stock/counts", new RecordCountRequest("A-01", StockItem.MaxQuantityPerLocation));
        full.EnsureSuccessStatusCode();
        var overflow = await client.PostAsJsonAsync($"/api/products/{product.Id}/stock/receipts", new ReceiveStockRequest("A-01", 1));
        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode);
        Assert.Equal("location_capacity_exceeded", (await ProblemJson.ReadAsync(overflow)).Code);

        Assert.Equal(StockItem.MaxQuantityPerLocation, (await client.GetProductAsync(product.Id)).OnHand);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products?limit=100")).StatusCode);
    }

    [Fact]
    public async Task NewProductWithThreshold_IsLowAndHasAnOpenAlert()
    {
        var client = factory.CreateWriterClient();

        var product = await client.CreateProductAsync("FRESH-1", reorderThreshold: 3);

        Assert.True(product.IsLowStock);
        var alerts = await client.GetFromJsonAsync<List<LowStockAlertResponse>>("/api/alerts/low-stock?limit=100");
        Assert.Contains(alerts!, a => a.ProductId == product.Id && a.AvailableAtRaise == 0 && a.ThresholdAtRaise == 3);
    }

    [Fact]
    public async Task StockList_IsPaged()
    {
        var client = factory.CreateWriterClient();
        var product = await client.CreateProductAsync("PAGED-1");
        foreach (var bin in new[] { "P-01", "P-02", "P-03" })
        {
            await client.ReceiveAsync(product.Id, bin, 1);
        }

        var first = await client.GetFromJsonAsync<List<StockLevelResponse>>("/api/stock?limit=2");
        var rest = await client.GetFromJsonAsync<List<StockLevelResponse>>("/api/stock?offset=2&limit=1000");

        Assert.Equal(2, first!.Count);
        Assert.Empty(first.Select(s => s.StockItemId).Intersect(rest!.Select(s => s.StockItemId)));
    }

    [Fact]
    public async Task RaisingReorderThreshold_RaisesLowStockAlertImmediately()
    {
        var client = factory.CreateWriterClient();
        var product = await client.CreateProductAsync("CABLE-1", reorderThreshold: 2);
        await client.ReceiveAsync(product.Id, "A-01", 5);

        var response = await client.PutAsJsonAsync($"/api/products/{product.Id}/reorder-threshold", new ChangeReorderThresholdRequest(8));

        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<ProductResponse>())!.IsLowStock);
        var alerts = await client.GetFromJsonAsync<List<LowStockAlertResponse>>("/api/alerts/low-stock");
        Assert.Contains(alerts!, a => a.ProductId == product.Id && a.ThresholdAtRaise == 8 && a.AvailableAtRaise == 5);

        var invalid = await client.PutAsJsonAsync($"/api/products/{product.Id}/reorder-threshold", new ChangeReorderThresholdRequest(-1));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
