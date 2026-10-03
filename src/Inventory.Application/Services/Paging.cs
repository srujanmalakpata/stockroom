namespace Inventory.Application.Services;

/// <summary>
/// Shared offset/limit rules for every list endpoint, so no query returns an unbounded result set.
/// Out-of-range values are clamped, not rejected: a negative offset becomes 0 and <c>limit</c> is forced
/// into 1..100 (so <c>limit=0</c> returns one item). This is documented in the OpenAPI summaries.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static (int Offset, int Limit) Clamp(int offset, int limit) =>
        (Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize));
}
