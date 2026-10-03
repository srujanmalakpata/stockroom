using Inventory.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inventory.Infrastructure.Persistence;

/// <summary>Commits the DbContext and translates provider errors into application exceptions.</summary>
internal sealed class EfUnitOfWork(InventoryDbContext db) : IUnitOfWork
{
    // SQLITE_CONSTRAINT_UNIQUE (extended result code); the primary result code 19 covers every constraint kind.
    private const int SqliteUniqueViolation = 2067;

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(
                "The data was changed by another request. Retry the operation.", ex);
        }
        catch (DbUpdateException ex) when (ViolatedUniqueIndex(ex) is { } index)
        {
            throw index switch
            {
                IndexNames.ProductSku => new ConflictException(
                    "duplicate_sku", "A product with the same SKU already exists."),

                // Both are races between requests that each decided from data that is now stale: two first
                // receipts into the same new bin, or two evaluations that both want to raise the one open
                // alert. Re-running the operation on fresh data resolves them, so they are retryable.
                IndexNames.StockItemLocation or IndexNames.OpenLowStockAlert => new ConcurrencyConflictException(
                    $"A concurrent request created the same record ({index}). Retry the operation.", ex),

                _ => new ConflictException("duplicate", "A record with the same unique key already exists."),
            };
        }
    }

    public void DiscardChanges() => db.ChangeTracker.Clear();

    /// <summary>The name of the unique index a failed save violated, or <c>null</c> if it was another error.</summary>
    private static string? ViolatedUniqueIndex(DbUpdateException ex) => ex.InnerException switch
    {
        // PostgreSQL reports the index name directly.
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg => pg.ConstraintName ?? IndexNames.Unknown,

        // SQLite reports the columns instead: "UNIQUE constraint failed: StockItems.ProductId, StockItems.LocationCode".
        SqliteException { SqliteExtendedErrorCode: SqliteUniqueViolation } sqlite => IndexNames.FromSqliteMessage(sqlite.Message),

        _ => null,
    };
}

/// <summary>Unique index names, shared by the EF configurations and the error translation above.</summary>
internal static class IndexNames
{
    public const string ProductSku = "IX_Products_Sku";
    public const string StockItemLocation = "IX_StockItems_ProductId_LocationCode";
    public const string OpenLowStockAlert = "IX_LowStockAlerts_ProductId_Open";
    public const string Unknown = "unknown";

    public static string FromSqliteMessage(string message) =>
        message.Contains("Products.Sku", StringComparison.Ordinal) ? ProductSku
        : message.Contains("StockItems.ProductId, StockItems.LocationCode", StringComparison.Ordinal) ? StockItemLocation
        : message.Contains("LowStockAlerts.ProductId", StringComparison.Ordinal) ? OpenLowStockAlert
        : Unknown;
}
