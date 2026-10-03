using Inventory.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class LowStockAlertConfiguration : IEntityTypeConfiguration<LowStockAlert>
{
    public void Configure(EntityTypeBuilder<LowStockAlert> builder)
    {
        builder.ToTable("LowStockAlerts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Sku).HasMaxLength(40).IsRequired();
        builder.Ignore(a => a.IsOpen);

        // At most one open alert per product (partial unique index; supported by SQLite and PostgreSQL).
        builder.HasIndex(a => a.ProductId)
            .IsUnique()
            .HasFilter("\"ResolvedAt\" IS NULL")
            .HasDatabaseName(IndexNames.OpenLowStockAlert);
    }
}
