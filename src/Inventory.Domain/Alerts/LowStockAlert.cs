using Inventory.Domain.Common;

namespace Inventory.Domain.Alerts;

/// <summary>Record that a product dropped below its reorder threshold; resolved when restocked.</summary>
public sealed class LowStockAlert
{
    private LowStockAlert()
    {
        Sku = string.Empty;
    }

    private LowStockAlert(Guid id, Guid productId, string sku, int availableAtRaise, int threshold, DateTimeOffset raisedAt)
    {
        Id = id;
        ProductId = productId;
        Sku = sku;
        AvailableAtRaise = availableAtRaise;
        Threshold = threshold;
        RaisedAt = Timestamps.Normalize(raisedAt);
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; }

    public int AvailableAtRaise { get; private set; }

    /// <summary>
    /// The reorder threshold in force when the alert was raised. It is a snapshot, like
    /// <see cref="AvailableAtRaise"/>: changing the product's threshold later does not rewrite it
    /// (the API exposes it as <c>thresholdAtRaise</c>). An open alert stays open while the product is
    /// still low under the new threshold and resolves as soon as it is not.
    /// </summary>
    public int Threshold { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public bool IsOpen => ResolvedAt is null;

    public static LowStockAlert Raise(Guid id, Guid productId, string sku, int available, int threshold, DateTimeOffset now) =>
        new(id, productId, sku, available, threshold, now);

    public void Resolve(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new DomainException(DomainErrorKind.RuleViolation, "alert_already_resolved", $"Alert {Id} is already resolved.");
        }

        ResolvedAt = Timestamps.Normalize(now);
    }
}
