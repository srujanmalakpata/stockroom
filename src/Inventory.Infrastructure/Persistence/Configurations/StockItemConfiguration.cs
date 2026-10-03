using Inventory.Domain.Products;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("StockItems", t =>
        {
            // Database-level backstop for the domain invariant: stock can never go negative.
            t.HasCheckConstraint("CK_StockItems_OnHand_NonNegative", "\"OnHand\" >= 0");
            t.HasCheckConstraint("CK_StockItems_Reserved_Range", "\"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\"");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.LocationCode).HasMaxLength(32).IsRequired();
        builder.HasIndex(s => new { s.ProductId, s.LocationCode }).IsUnique().HasDatabaseName(IndexNames.StockItemLocation);
        builder.HasIndex(s => s.LocationCode);
        builder.HasOne<Product>().WithMany().HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(s => s.Available);

        // Optimistic concurrency: UPDATE ... WHERE "Id" = @id AND "Version" = @originalVersion.
        // Zero rows affected => DbUpdateConcurrencyException => the operation is retried on fresh data.
        builder.Property(s => s.Version).IsConcurrencyToken();
    }
}
