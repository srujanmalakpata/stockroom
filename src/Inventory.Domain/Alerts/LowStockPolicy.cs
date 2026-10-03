namespace Inventory.Domain.Alerts;

public enum LowStockDecision
{
    NoChange,
    Raise,
    Resolve,
}

/// <summary>
/// Pure rule: a product is "low" when total available units across all locations fall below its
/// reorder threshold. Alerts are edge-triggered: one open alert per product until stock recovers.
/// Totals are <see cref="long"/> because a product's locations can together exceed <see cref="int.MaxValue"/>.
/// </summary>
public static class LowStockPolicy
{
    public static bool IsLow(long totalAvailable, int reorderThreshold) => totalAvailable < reorderThreshold;

    public static LowStockDecision Evaluate(long totalAvailable, int reorderThreshold, bool hasOpenAlert) =>
        (IsLow(totalAvailable, reorderThreshold), hasOpenAlert) switch
        {
            (true, false) => LowStockDecision.Raise,
            (false, true) => LowStockDecision.Resolve,
            _ => LowStockDecision.NoChange,
        };
}
