using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Persistence;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", CatalogModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_categories_slug", CatalogSql.SlugCheck("slug"));
            table.HasCheckConstraint("ck_categories_name", CatalogSql.LengthCheck("name", Category.NameMaxLength));
            table.HasCheckConstraint("ck_categories_not_own_parent", "parent_id IS NULL OR parent_id <> id");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Slug).IsRequired();
        builder.Property(c => c.Name).IsRequired();
        builder.Property(c => c.SortOrder).HasDefaultValue(0);
        builder.Property(c => c.IsActive).HasDefaultValue(true).HasSentinel(true);
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(c => c.UpdatedAt).HasDefaultValueSql("now()");

        // Xoá danh mục cha khi còn con ⇒ lỗi; muốn bỏ thì xoá mềm.
        builder.HasOne(c => c.Parent)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.Slug).IsUnique().HasFilter(CatalogSql.NotDeleted);
        builder.HasQueryFilter(c => c.DeletedAt == null);
    }
}
