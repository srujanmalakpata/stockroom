using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inventory.Application.Contracts;

namespace Inventory.Api.IntegrationTests;

public sealed partial class PlatformTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReportHealthy(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApiDocument_DescribesEndpointsAndApiKeyScheme()
    {
        var json = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        using var doc = JsonDocument.Parse(json);

        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/orders", out _));
        Assert.True(paths.TryGetProperty("/api/products/{id}/stock/receipts", out _));
        var scheme = doc.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());

        // Only write operations are marked as needing the key; anonymous reads are not.
        var products = paths.GetProperty("/api/products");
        Assert.True(products.GetProperty("post").TryGetProperty("security", out _));
        Assert.False(products.GetProperty("get").TryGetProperty("security", out _));
        Assert.False(doc.RootElement.TryGetProperty("security", out _));
    }

    [Fact]
    public async Task ProblemTraceIds_AreW3CTraceIds_FromTheRequestActivity()
    {
        // In a real host, logging/OpenTelemetry makes ASP.NET Core start an Activity per request; the
        // test host has no logging, so listen to its ActivitySource to get the same behaviour.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", InventoryApiFactory.ApiKey);

        var notFound = await client.GetAsync("/api/does-not-exist"); // status-code page
        var invalid = await client.PostAsJsonAsync("/api/products", new CreateProductRequest("x", "", 0)); // validation filter
        var missing = await client.GetAsync($"/api/orders/{Guid.NewGuid()}"); // exception handler

        foreach (var response in new[] { notFound, invalid, missing })
        {
            var traceId = (await ProblemJson.ReadAsync(response)).Raw.GetProperty("traceId").GetString();
            Assert.Matches(W3CTraceParent(), traceId);
        }
    }

    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-0[0-9a-f]$")]
    private static partial Regex W3CTraceParent();
}
