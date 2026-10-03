namespace Inventory.Domain.Common;

/// <summary>
/// Every instant the domain stores goes through <see cref="Normalize"/>: UTC (offset zero) and whole
/// milliseconds. SQLite stores instants with 0.1 ms precision and PostgreSQL with 1 µs, so without
/// this a response built from the in-memory entity could differ from the same row read back later,
/// and SQLite's integer encoding (local ticks + offset) only sorts correctly when every offset is zero.
/// </summary>
public static class Timestamps
{
    public static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
}
