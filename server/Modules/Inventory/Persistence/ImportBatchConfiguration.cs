using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;
using server.Modules.Identity.Domain;
using server.Modules.Inventory.Domain;

namespace server.Modules.Inventory.Persistence;

internal sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        string admin = $"'{SnakeCaseEnumConverter<ImportSource>.ToProvider(ImportSource.Admin)}'";

        builder.ToTable("import_batches", InventoryModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_import_batches_source", SnakeCaseEnumConverter<ImportSource>.CheckSql("source"));
            table.HasCheckConstraint("ck_import_batches_importer", $"source <> {admin} OR imported_by IS NOT NULL");
            table.HasCheckConstraint("ck_import_batches_counts",
                "total_rows >= 0 AND inserted_rows >= 0 AND duplicate_rows >= 0 AND invalid_rows >= 0 " +
                "AND inserted_rows + duplicate_rows + invalid_rows = total_rows");
            table.HasCheckConstraint("ck_import_batches_texts",
                "char_length(file_name) BETWEEN 1 AND 255 AND (note IS NULL OR char_length(note) <= 500)");
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Source).HasConversion(new SnakeCaseEnumConverter<ImportSource>());
        builder.Property(b => b.FileName).IsRequired();
        builder.Property(b => b.CreatedAt).HasDefaultValueSql("now()");

        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(b => b.VariantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.ImportedBy).OnDelete(DeleteBehavior.Restrict);

        // Lịch sử nhập của một gói, mới nhất trước; thay index FK mặc định.
        builder.HasIndex(b => new { b.VariantId, b.CreatedAt }).IsDescending(false, true)
            .HasDatabaseName("ix_import_batches_variant_created");
        builder.HasIndex(b => b.ImportedBy).HasDatabaseName("ix_import_batches_imported_by").HasFilter("imported_by IS NOT NULL");
    }
}
