using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Inventory.Infrastructure.Persistence;

// Used only by `dotnet ef migrations add`; never at runtime. Connection strings here are local
// placeholders: generating a migration does not connect to the database.

internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteInventoryDbContext>
{
    public SqliteInventoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteInventoryDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options);
}

internal sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresInventoryDbContext>
{
    public PostgresInventoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresInventoryDbContext>()
            .UseNpgsql("Host=localhost;Database=inventory_design")
            .Options);
}
