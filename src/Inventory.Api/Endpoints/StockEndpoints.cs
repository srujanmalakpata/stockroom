using Inventory.Application.Services;

namespace Inventory.Api.Endpoints;

public static class StockEndpoints
{
    public static RouteGroupBuilder MapStockEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/stock", async (
                StockService service,
                CancellationToken ct,
                string? location = null,
                int offset = 0,
                int limit = Paging.DefaultPageSize) =>
                TypedResults.Ok(await service.ListAsync(location, offset, limit, ct)))
            .WithTags("Stock")
            .WithSummary("List stock levels (paged by location; limit clamped to 1-100), optionally for one location.");

        api.MapGet("/alerts/low-stock", async (
                AlertService service,
                CancellationToken ct,
                bool includeResolved = false,
                int offset = 0,
                int limit = Paging.DefaultPageSize) =>
                TypedResults.Ok(await service.ListAsync(includeResolved, offset, limit, ct)))
            .WithTags("Alerts")
            .WithSummary("Low-stock alerts, newest first (open only by default; paged, limit clamped to 1-100). thresholdAtRaise is a snapshot.");

        return api;
    }
}
