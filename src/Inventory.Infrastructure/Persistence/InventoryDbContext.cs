using Inventory.Domain.Alerts;
using Inventory.Domain.Orders;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Shared EF Core model. Each database provider has a thin subclass (<see cref="SqliteInventoryDbContext"/>,
/// <see cref="PostgresInventoryDbContext"/>) so that each keeps its own migration history; the model
/// itself is defined once here.
/// </summary>
public abstract class InventoryDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<LowStockAlert> LowStockAlerts => Set<LowStockAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
}

public sealed class SqliteInventoryDbContext(DbContextOptions<SqliteInventoryDbContext> options)
    : InventoryDbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native DateTimeOffset type and EF cannot ORDER BY the default TEXT mapping.
        // Store instants as 64-bit integers instead. The converter packs (local ticks / 1000) and the
        // offset, so it keeps 0.1 ms precision and sorts correctly only when every offset is zero; the
        // domain guarantees both by normalising every instant to UTC whole milliseconds (Timestamps).
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
    }
}

public sealed class PostgresInventoryDbContext(DbContextOptions<PostgresInventoryDbContext> options)
    : InventoryDbContext(options);
