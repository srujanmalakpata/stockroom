namespace Inventory.Domain.Common;

/// <summary>Raised when a reservation asks for more units than are available.</summary>
public sealed class InsufficientStockException : DomainException
{
    public InsufficientStockException(Guid productId, int requested, int available)
        : base(
            DomainErrorKind.RuleViolation,
            "insufficient_stock",
            $"Product {productId} has {available} unit(s) available but {requested} were requested.")
    {
        ProductId = productId;
        Requested = requested;
        Available = available;
    }

    public Guid ProductId { get; }

    public int Requested { get; }

    public int Available { get; }
}
