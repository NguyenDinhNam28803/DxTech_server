using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Persistence;

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("product_variants", CatalogModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_product_variants_sku", "sku ~ '^[A-Za-z0-9][A-Za-z0-9._-]*$'");
            table.HasCheckConstraint("ck_product_variants_name", CatalogSql.LengthCheck("name", 200));
            table.HasCheckConstraint("ck_product_variants_term_months", "term_months IS NULL OR term_months > 0");
            table.HasCheckConstraint("ck_product_variants_seats", "seats > 0");
            table.HasCheckConstraint("ck_product_variants_price", "price_vnd >= 0");
            table.HasCheckConstraint("ck_product_variants_compare_at_price",
                "compare_at_price_vnd IS NULL OR compare_at_price_vnd > price_vnd");
            table.HasCheckConstraint("ck_product_variants_delivery_type",
                SnakeCaseEnumConverter<DeliveryType>.CheckSql("delivery_type"));
        });

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.Property(v => v.Sku).IsRequired();
        builder.Property(v => v.Name).IsRequired();
        builder.Property(v => v.Seats).HasDefaultValue(1).HasSentinel(1);
        builder.Property(v => v.DeliveryType).HasConversion(new SnakeCaseEnumConverter<DeliveryType>());
        builder.Property(v => v.IsActive).HasDefaultValue(true).HasSentinel(true);
        builder.Property(v => v.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(v => v.UpdatedAt).HasDefaultValueSql("now()");

        // Cột hệ thống xmin làm concurrency token (D9). Đặt tên tường minh để convention snake_case không đổi thành "version".
        builder.Property(v => v.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        builder.HasOne(v => v.Product)
            .WithMany(p => p.Variants)
            .HasForeignKey(v => v.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(v => v.Sku).IsUnique().HasFilter(CatalogSql.NotDeleted);

        // Giá thấp nhất của sản phẩm ("Từ 690.000đ") và lọc theo khoảng giá. Thay index mặc định trên product_id.
        builder.HasIndex(v => new { v.ProductId, v.PriceVnd });

        // Mỗi sản phẩm tối đa một gói mặc định.
        builder.HasIndex(v => v.ProductId)
            .HasDatabaseName("ux_product_variants_one_default")
            .IsUnique()
            .HasFilter($"is_default AND {CatalogSql.NotDeleted}");

        builder.HasQueryFilter(v => v.DeletedAt == null);
    }
}
