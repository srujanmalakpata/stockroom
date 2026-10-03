using Inventory.Domain.Orders;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.IntegrationTests;

/// <summary>
/// Guards the EF model settings that a migration-drift check cannot see: a concurrency token on an
/// existing int column changes the generated UPDATE statements, not the schema, so
/// <c>dotnet ef migrations has-pending-model-changes</c> stays green if someone deletes it.
/// </summary>
public sealed class ModelTests
{
    public static TheoryData<string, Type> TokenColumns() => new()
    {
        { "Sqlite", typeof(StockItem) }, { "Sqlite", typeof(Order) }, { "Sqlite", typeof(Product) },
        { "Postgres", typeof(StockItem) }, { "Postgres", typeof(Order) }, { "Postgres", typeof(Product) },
    };

    [Theory]
    [MemberData(nameof(TokenColumns))]
    public void VersionColumns_AreConcurrencyTokens(string provider, Type entity)
    {
        // Building the model needs no database connection.
        using InventoryDbContext db = provider == "Sqlite"
            ? new SqliteInventoryDbContext(new DbContextOptionsBuilder<SqliteInventoryDbContext>().UseSqlite("Data Source=:memory:").Options)
            : new PostgresInventoryDbContext(new DbContextOptionsBuilder<PostgresInventoryDbContext>().UseNpgsql("Host=unused").Options);

        var version = db.Model.FindEntityType(entity)!.FindProperty("Version");

        Assert.NotNull(version);
        Assert.True(version.IsConcurrencyToken, $"{entity.Name}.Version must be a concurrency token on {provider}.");
    }
}
