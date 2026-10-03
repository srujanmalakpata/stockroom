using System.Net.Http.Json;
using Inventory.Application.Contracts;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Inventory.Api.IntegrationTests;

/// <summary>
/// Hosts the real API (all layers, real EF Core migrations) against a private database per test class.
/// <para>
/// Default: a named SQLite in-memory database. Every DbContext opens its own connection to the
/// shared-cache database, just like separate requests would against a file or server database; a
/// keep-alive connection held by the factory stops SQLite discarding it between requests.
/// </para>
/// <para>
/// If <c>INVENTORY_TEST_POSTGRES</c> is set to a PostgreSQL connection string (CI does this with a
/// service container), the same tests run against a fresh PostgreSQL database that is dropped afterwards.
/// </para>
/// </summary>
public sealed class InventoryApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-test-key";
    public const string PostgresVariable = "INVENTORY_TEST_POSTGRES";

    private readonly string _provider;
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    public InventoryApiFactory()
    {
        var postgres = Environment.GetEnvironmentVariable(PostgresVariable);
        if (string.IsNullOrWhiteSpace(postgres))
        {
            _provider = "Sqlite";
            _connectionString = $"Data Source=inventory-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        else
        {
            _provider = "Postgres";
            _connectionString = new NpgsqlConnectionStringBuilder(postgres) { Database = $"inventory_test_{Guid.NewGuid():N}" }.ConnectionString;
        }
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Client that sends the API key, i.e. is allowed to call write endpoints.</summary>
    public HttpClient CreateWriterClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting (host configuration) is visible to Program.cs before the app is built.
        builder.UseSetting("ConnectionStrings:Inventory", _connectionString);
        builder.UseSetting("Database:Provider", _provider);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:ApiKeys:0", ApiKey);
        builder.UseSetting("OpenApi:Enabled", "true");
        builder.UseSetting("Concurrency:MaxAttempts", "10");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        if (_keepAlive is null)
        {
            // PostgreSQL: drop the per-class database so repeated runs leave nothing behind.
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.EnsureDeletedAsync();
        }

        await base.DisposeAsync();
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
        }
    }
}

internal static class HttpClientExtensions
{
    public static async Task<ProductResponse> CreateProductAsync(this HttpClient client, string sku, int reorderThreshold = 0)
    {
        var response = await client.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, $"Product {sku}", reorderThreshold));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    public static async Task<ProductResponse> ReceiveAsync(this HttpClient client, Guid productId, string location, int quantity)
    {
        var response = await client.PostAsJsonAsync($"/api/products/{productId}/stock/receipts", new ReceiveStockRequest(location, quantity));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    public static Task<HttpResponseMessage> PlaceOrderAsync(this HttpClient client, string customer, params (Guid ProductId, int Quantity)[] lines) =>
        client.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest(customer, lines.Select(l => new PlaceOrderLineRequest(l.ProductId, l.Quantity)).ToList()));

    public static async Task<ProductResponse> GetProductAsync(this HttpClient client, Guid productId) =>
        (await client.GetFromJsonAsync<ProductResponse>($"/api/products/{productId}"))!;
}
