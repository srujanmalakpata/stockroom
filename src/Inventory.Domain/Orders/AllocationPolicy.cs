using Inventory.Domain.Common;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Orders;

/// <summary>
/// Decides which locations a requested quantity is picked from. Pure function: it reads stock but
/// does not change it, so an order can be checked in full before any reservation is made.
/// </summary>
public static class AllocationPolicy
{
    /// <summary>
    /// Greedy "largest available first": fills from the location with the most available units,
    /// which keeps the number of pick locations (split picks) low. Ties break on location code so the
    /// result is deterministic.
    /// </summary>
    /// <exception cref="InsufficientStockException">Total available is less than requested.</exception>
    public static IReadOnlyList<(StockItem Stock, int Quantity)> Allocate(
        Guid productId,
        int quantity,
        IEnumerable<StockItem> stock)
    {
        Guard.Positive(quantity, "Requested quantity");

        var candidates = stock
            .Where(s => s.ProductId == productId && s.Available > 0)
            .OrderByDescending(s => s.Available)
            .ThenBy(s => s.LocationCode, StringComparer.Ordinal)
            .ToList();

        // long: many locations near capacity could exceed int.MaxValue in total.
        var totalAvailable = candidates.Sum(s => (long)s.Available);
        if (totalAvailable < quantity)
        {
            // Here totalAvailable < quantity <= int.MaxValue, so the cast is lossless.
            throw new InsufficientStockException(productId, quantity, (int)totalAvailable);
        }

        var plan = new List<(StockItem, int)>();
        var remaining = quantity;
        foreach (var item in candidates)
        {
            var take = Math.Min(item.Available, remaining);
            plan.Add((item, take));
            remaining -= take;
            if (remaining == 0)
            {
                break;
            }
        }

        return plan;
    }
}
