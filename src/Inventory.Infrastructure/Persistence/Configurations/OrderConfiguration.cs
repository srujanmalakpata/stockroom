using Inventory.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.CustomerReference).HasMaxLength(Order.MaxCustomerReferenceLength).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(o => o.Status);
        builder.Property(o => o.Version).IsConcurrencyToken();

        builder.OwnsMany(o => o.Lines, lines =>
        {
            lines.ToTable("OrderLines");
            lines.WithOwner().HasForeignKey("OrderId");
            lines.Property<int>("Id");
            lines.HasKey("Id");
        });
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(o => o.Reservations, reservations =>
        {
            reservations.ToTable("StockReservations");
            reservations.WithOwner().HasForeignKey("OrderId");
            reservations.Property<int>("Id");
            reservations.HasKey("Id");
            reservations.Property(r => r.LocationCode).HasMaxLength(32).IsRequired();
            reservations.HasIndex(r => r.StockItemId);
        });
        builder.Navigation(o => o.Reservations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
