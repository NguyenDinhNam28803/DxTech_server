using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Persistence;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <summary>Shadow property cho cột generated <c>search_vector</c>.</summary>
    public const string SearchVector = "SearchVector";

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        string platforms = string.Join(", ", Platforms.All.Select(p => $"'{p}'"));

        builder.ToTable("products", CatalogModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_products_slug", CatalogSql.SlugCheck("slug"));
            table.HasCheckConstraint("ck_products_name", CatalogSql.LengthCheck("name", Product.NameMaxLength));
            table.HasCheckConstraint("ck_products_license_type", SnakeCaseEnumConverter<LicenseType>.CheckSql("license_type"));
            table.HasCheckConstraint("ck_products_platforms", $"platforms <@ ARRAY[{platforms}]::text[]");
            table.HasCheckConstraint("ck_products_features_array", "jsonb_typeof(features) = 'array'");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Slug).IsRequired();
        builder.Property(p => p.Name).IsRequired();
        builder.Property(p => p.LicenseType).HasConversion(new SnakeCaseEnumConverter<LicenseType>());
        builder.Property(p => p.Platforms).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
        builder.Property(p => p.Features)
            .HasColumnType("jsonb")
            .HasConversion(JsonListConverter.Converter, JsonListConverter.Comparer)
            .HasDefaultValueSql("'[]'::jsonb");
        builder.Property(p => p.IsActive).HasDefaultValue(true).HasSentinel(true);
        builder.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");

        builder.Property<NpgsqlTsVector>(SearchVector)
            .HasColumnName("search_vector")
            .HasComputedColumnSql(CatalogSql.ProductSearchVector, stored: true);

        builder.HasOne(p => p.Category)
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.Slug).IsUnique().HasFilter(CatalogSql.NotDeleted);

        // Lọc theo danh mục trên trang Catalog: chỉ sản phẩm đang bán (index nhỏ hơn, đúng truy vấn).
        builder.HasIndex(p => p.CategoryId)
            .HasDatabaseName("ix_products_category_id_listed")
            .HasFilter($"{CatalogSql.NotDeleted} AND is_active");

        builder.HasIndex(p => p.Platforms).HasMethod("gin");
        builder.HasIndex(SearchVector).HasMethod("gin");

        builder.HasQueryFilter(p => p.DeletedAt == null);
    }
}
