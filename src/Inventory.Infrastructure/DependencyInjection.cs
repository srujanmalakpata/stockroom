using Inventory.Application.Abstractions;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Apply pending EF migrations at startup. Convenient for dev/tests; use a migration bundle in production.</summary>
    public bool MigrateOnStartup { get; set; }
}

public static class DependencyInjection
{
    public const string ConnectionStringName = "Inventory";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddSingleton(options);

        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        switch (options.Provider)
        {
            case DatabaseProvider.Postgres:
                // Transient failures (an Azure failover, a dropped connection) are retried by Npgsql's execution
                // strategy. That is compatible because the app never opens its own transactions: each
                // SaveChanges is one implicit transaction. Optimistic-concurrency conflicts are not transient
                // and are still handled by ConcurrencyRetry.
                services.AddDbContext<PostgresInventoryDbContext>(o => o.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));
                services.AddScoped<InventoryDbContext>(sp => sp.GetRequiredService<PostgresInventoryDbContext>());
                break;
            case DatabaseProvider.Sqlite:
            default:
                services.AddDbContext<SqliteInventoryDbContext>(o => o.UseSqlite(connectionString));
                services.AddScoped<InventoryDbContext>(sp => sp.GetRequiredService<SqliteInventoryDbContext>());
                break;
        }

        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        return services;
    }

    /// <summary>Applies pending migrations when <see cref="DatabaseOptions.MigrateOnStartup"/> is set.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        if (!services.GetRequiredService<DatabaseOptions>().MigrateOnStartup)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync(ct);
    }
}
