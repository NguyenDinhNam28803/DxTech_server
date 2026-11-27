using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;
using server.Modules.Sales.Domain;

namespace server.Modules.Sales.Persistence;

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", SalesModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_order_items_delivery_type", SnakeCaseEnumConverter<ItemDeliveryType>.CheckSql("delivery_type"));
            table.HasCheckConstraint("ck_order_items_quantity", $"quantity BETWEEN 1 AND {OrderItem.MaxQuantity}");
            table.HasCheckConstraint("ck_order_items_price", "unit_price_vnd >= 0");
            table.HasCheckConstraint("ck_order_items_line_total", "line_total_vnd = unit_price_vnd * quantity");
            table.HasCheckConstraint("ck_order_items_snapshots",
                "char_length(product_name_snapshot) BETWEEN 1 AND 200 AND char_length(variant_name_snapshot) BETWEEN 1 AND 200 " +
                "AND char_length(sku_snapshot) BETWEEN 1 AND 64");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.ProductNameSnapshot).IsRequired();
        builder.Property(i => i.VariantNameSnapshot).IsRequired();
        builder.Property(i => i.SkuSnapshot).IsRequired();
        builder.Property(i => i.DeliveryType).HasConversion(new SnakeCaseEnumConverter<ItemDeliveryType>());

        // FK chéo schema sang catalog.product_variants (cùng DB — ADR-002), không navigation.
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(i => i.VariantId).OnDelete(DeleteBehavior.Restrict);

        // Mỗi gói một dòng trong đơn (gộp số lượng); cũng là index của FK order_id.
        builder.HasIndex(i => new { i.OrderId, i.VariantId }).IsUnique().HasDatabaseName("ux_order_items_order_variant");
        // Doanh thu theo gói; kiểm tra FK khi xoá cứng gói.
        builder.HasIndex(i => i.VariantId).HasDatabaseName("ix_order_items_variant_id");
    }
}
