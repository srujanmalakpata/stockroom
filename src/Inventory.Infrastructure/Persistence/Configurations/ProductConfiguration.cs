using Inventory.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Sku).HasMaxLength(40).IsRequired();
        builder.HasIndex(p => p.Sku).IsUnique().HasDatabaseName(IndexNames.ProductSku);
        builder.Property(p => p.Name).HasMaxLength(Product.MaxNameLength).IsRequired();

        // Bumped on threshold changes and on every stock change for this product (see Product.Version),
        // so concurrent writers to different locations of one product conflict instead of interleaving.
        builder.Property(p => p.Version).IsConcurrencyToken();
    }
}
