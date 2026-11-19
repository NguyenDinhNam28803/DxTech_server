using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using server.Infrastructure.Persistence.Conventions;

namespace server.UnitTests.Infrastructure;

public class SnakeCaseNamingConventionTests
{
    [Theory]
    [InlineData("ProductVariants", "product_variants")]
    [InlineData("CategoryId", "category_id")]
    [InlineData("PK_Products", "pk_products")]
    [InlineData("FK_Products_Categories_CategoryId", "fk_products_categories_category_id")]
    [InlineData("IX_Products_CategoryId", "ix_products_category_id")]
    [InlineData("HTMLParser", "html_parser")]
    [InlineData("Sha256Hash", "sha256_hash")]
    [InlineData("PriceVnd", "price_vnd")]
    [InlineData("xmin", "xmin")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("Id", "id")]
    [InlineData("", "")]
    public void Converts_names(string input, string expected)
    {
        Assert.Equal(expected, SnakeCaseNamingConvention.ToSnakeCase(input));
    }

    [Fact]
    public void Applies_to_tables_columns_keys_foreign_keys_and_indexes()
    {
        using var context = new ConventionTestContext();
        IModel model = context.Model;

        // Không có DbSet nên EF đặt tên bảng theo tên class (số ít). Module thật luôn gọi
        // ToTable("product_variants", "catalog") tường minh — xem test Explicit_plural_table_name_drives_key_and_index_names.
        IEntityType variant = model.FindEntityType(typeof(ProductVariant))!;
        Assert.Equal("product_variant", variant.GetTableName());
        Assert.Equal("catalog", variant.GetSchema());
        Assert.Equal("price_vnd", variant.FindProperty(nameof(ProductVariant.PriceVnd))!.GetColumnName());
        Assert.Equal("product_id", variant.FindProperty(nameof(ProductVariant.ProductId))!.GetColumnName());
        Assert.Equal("pk_product_variant", variant.FindPrimaryKey()!.GetName());
        Assert.Equal(
            "fk_product_variant_products_product_id",
            variant.GetForeignKeys().Single().GetConstraintName());
        Assert.Equal(
            "ix_product_variant_product_id",
            variant.GetIndexes().Single(i => i.Properties.Single().Name == nameof(ProductVariant.ProductId)).GetDatabaseName());
    }

    [Fact]
    public void Explicit_plural_table_name_drives_key_and_index_names()
    {
        using var context = new ConventionTestContext();
        IEntityType product = context.Model.FindEntityType(typeof(Product))!;

        Assert.Equal("products", product.GetTableName());
        Assert.Equal("pk_products", product.FindPrimaryKey()!.GetName());
        Assert.Equal("name", product.FindProperty(nameof(Product.Name))!.GetColumnName());
    }

    [Fact]
    public void Keeps_names_that_were_configured_explicitly()
    {
        using var context = new ConventionTestContext();
        IEntityType legacy = context.Model.FindEntityType(typeof(LegacyRow))!;

        Assert.Equal("Legacy_Table", legacy.GetTableName());
        Assert.Equal("CustomColumn", legacy.FindProperty(nameof(LegacyRow.Value))!.GetColumnName());
        Assert.Equal("legacy_id", legacy.FindProperty(nameof(LegacyRow.LegacyId))!.GetColumnName());
    }

    private sealed class Product
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<ProductVariant> Variants { get; set; } = [];
    }

    private sealed class ProductVariant
    {
        public Guid Id { get; set; }
        public Guid ProductId { get; set; }
        public long PriceVnd { get; set; }
    }

    private sealed class LegacyRow
    {
        public int LegacyId { get; set; }
        public string Value { get; set; } = "";
    }

    /// <summary>Chỉ dựng model (không mở kết nối) để kiểm tra tên sinh ra.</summary>
    private sealed class ConventionTestContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql("Host=localhost;Database=model_only");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("catalog");
            modelBuilder.Entity<Product>().ToTable("products").HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId);
            modelBuilder.Entity<ProductVariant>();
            modelBuilder.Entity<LegacyRow>(b =>
            {
                b.ToTable("Legacy_Table");
                b.HasKey(r => r.LegacyId);
                b.Property(r => r.Value).HasColumnName("CustomColumn");
            });
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.Conventions.Add(_ => new SnakeCaseNamingConvention());
    }
}
