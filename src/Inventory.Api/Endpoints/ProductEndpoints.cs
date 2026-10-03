using Inventory.Api.Auth;
using Inventory.Api.Http;
using Inventory.Application.Contracts;
using Inventory.Application.Services;

namespace Inventory.Api.Endpoints;

public static class ProductEndpoints
{
    public static RouteGroupBuilder MapProductEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/products").WithTags("Products");

        group.MapGet("/", async (ProductService service, CancellationToken ct, int offset = 0, int limit = Paging.DefaultPageSize) =>
                TypedResults.Ok(await service.ListAsync(offset, limit, ct)))
            .WithSummary("List products with stock totals (paged by SKU; offset < 0 becomes 0, limit is clamped to 1-100).");

        group.MapGet("/{id:guid}", async (Guid id, ProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetProduct")
            .WithSummary("Get a product with stock per location.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (CreateProductRequest request, ProductService service, CancellationToken ct) =>
            {
                var product = await service.CreateAsync(request, ct);
                return TypedResults.CreatedAtRoute(product, "GetProduct", new { id = product.Id });
            })
            .RequireAuthorization(ApiKeyDefaults.WritePolicy)
            .Validate<CreateProductRequest>()
            .WithSummary("Create a product.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/reorder-threshold",
                async (Guid id, ChangeReorderThresholdRequest request, ProductService service, CancellationToken ct) =>
                    TypedResults.Ok(await service.ChangeReorderThresholdAsync(id, request, ct)))
            .RequireAuthorization(ApiKeyDefaults.WritePolicy)
            .Validate<ChangeReorderThresholdRequest>()
            .WithSummary("Change the low-stock reorder threshold.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/stock/receipts",
                async (Guid id, ReceiveStockRequest request, StockService service, CancellationToken ct) =>
                    TypedResults.Ok(await service.ReceiveAsync(id, request, ct)))
            .RequireAuthorization(ApiKeyDefaults.WritePolicy)
            .Validate<ReceiveStockRequest>()
            .WithSummary("Receive stock into a warehouse location.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/stock/counts",
                async (Guid id, RecordCountRequest request, StockService service, CancellationToken ct) =>
                    TypedResults.Ok(await service.RecordCountAsync(id, request, ct)))
            .RequireAuthorization(ApiKeyDefaults.WritePolicy)
            .Validate<RecordCountRequest>()
            .WithSummary("Record a cycle count (sets on-hand to the counted quantity).")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
