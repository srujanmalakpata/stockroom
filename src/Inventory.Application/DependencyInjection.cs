using FluentValidation;
using Inventory.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inventory.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, ConcurrencyOptions? concurrency = null)
    {
        services.AddSingleton(concurrency ?? new ConcurrencyOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ConcurrencyRetry>();
        services.AddScoped<LowStockMonitor>();
        services.AddScoped<ProductService>();
        services.AddScoped<StockService>();
        services.AddScoped<OrderService>();
        services.AddScoped<AlertService>();
        services.AddValidatorsFromAssemblyContaining<ProductService>(ServiceLifetime.Singleton);
        return services;
    }
}
