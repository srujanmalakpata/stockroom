using Inventory.Api.Auth;
using Inventory.Api.Http;
using Inventory.Application.Contracts;
using Inventory.Application.Services;

namespace Inventory.Api.Endpoints;

public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/orders").WithTags("Orders");

        group.MapPost("/", async (PlaceOrderRequest request, OrderService service, CancellationToken ct) =>
            {
                var order = await service.PlaceAsync(request, ct);
                return TypedResults.CreatedAtRoute(order, "GetOrder", new { id = order.Id });
            })
            .RequireAuthorization(ApiKeyDefaults.WritePolicy)
            .Validate<PlaceOrderRequest>()
            .WithSummary("Place an order; reserves stock for every line or fails with 409 insufficient_stock.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", async (Guid id, OrderService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetOrder")
            .WithSummary("Get an order with its stock reservations.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/fulfil", async (Guid id, OrderService service, CancellationToken ct) =>
                TypedResults.Ok(await service.FulfilAsync(id, ct)))
            .RequireAuthorization(ApiKeyDefaults.WritePolicy)
            .WithSummary("Ship the order: reserved units leave on-hand stock.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/cancel", async (Guid id, OrderService service, CancellationToken ct) =>
                TypedResults.Ok(await service.CancelAsync(id, ct)))
            .RequireAuthorization(ApiKeyDefaults.WritePolicy)
            .WithSummary("Cancel the order: reserved units are released.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
