namespace Inventory.Application.Contracts;

public sealed record CreateProductRequest(string Sku, string Name, int ReorderThreshold);

public sealed record ChangeReorderThresholdRequest(int ReorderThreshold);

public sealed record ReceiveStockRequest(string LocationCode, int Quantity);

public sealed record RecordCountRequest(string LocationCode, int CountedQuantity);

public sealed record PlaceOrderLineRequest(Guid ProductId, int Quantity);

public sealed record PlaceOrderRequest(string CustomerReference, IReadOnlyList<PlaceOrderLineRequest> Lines);
