using System.Text.RegularExpressions;
using Inventory.Domain.Common;

namespace Inventory.Domain.Products;

/// <summary>A stock-keeping unit the warehouse carries. Stock levels live in <see cref="Stock.StockItem"/>.</summary>
public sealed partial class Product
{
    public const int MaxNameLength = 200;

    private Product()
    {
        Sku = string.Empty;
        Name = string.Empty;
    }

    private Product(Guid id, string sku, string name, int reorderThreshold, DateTimeOffset createdAt)
    {
        Id = id;
        Sku = sku;
        Name = name;
        ReorderThreshold = reorderThreshold;
        CreatedAt = Timestamps.Normalize(createdAt);
    }

    public Guid Id { get; private set; }

    /// <summary>Upper-case, unique business identifier such as <c>WIDGET-RED-01</c>.</summary>
    public string Sku { get; private set; }

    public string Name { get; private set; }

    /// <summary>A low-stock alert is raised when total available units fall below this value.</summary>
    public int ReorderThreshold { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Optimistic concurrency token for the product <em>and its stock as a whole</em>. It is bumped when
    /// the threshold changes and whenever any of the product's stock rows change (see
    /// <see cref="MarkStockChanged"/>), so two requests that each read all of a product's locations,
    /// for example to decide on a low-stock alert, cannot both commit a decision based on a stale total.
    /// </summary>
    public int Version { get; private set; }

    public static Product Create(Guid id, string sku, string name, int reorderThreshold, DateTimeOffset now)
    {
        var normalizedSku = NormalizeSku(sku);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            throw new DomainException(DomainErrorKind.InvalidInput, "invalid_name", $"Product name must be 1-{MaxNameLength} characters.");
        }

        return new Product(
            id,
            normalizedSku,
            name.Trim(),
            Guard.NotNegative(reorderThreshold, "Reorder threshold"),
            now);
    }

    public void ChangeReorderThreshold(int reorderThreshold)
    {
        ReorderThreshold = Guard.NotNegative(reorderThreshold, "Reorder threshold");
        Version++;
    }

    /// <summary>
    /// Records that stock for this product changed at some location. Writers to different locations
    /// of the same product then conflict on this row instead of silently interleaving (write skew).
    /// </summary>
    public void MarkStockChanged() => Version++;

    public static bool IsValidSku(string? sku) =>
        sku is not null && SkuPattern().IsMatch(sku.Trim().ToUpperInvariant());

    public static string NormalizeSku(string sku)
    {
        if (!IsValidSku(sku))
        {
            throw new DomainException(
                DomainErrorKind.InvalidInput,
                "invalid_sku",
                "SKU must be 2-40 characters: letters, digits, '-', '_' or '.', starting with a letter or digit.");
        }

        return sku.Trim().ToUpperInvariant();
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
}
