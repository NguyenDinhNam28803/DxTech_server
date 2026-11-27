using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;
using server.Modules.Inventory.Domain;
using server.Modules.Sales.Domain;

namespace server.Modules.Inventory.Persistence;

internal sealed class LicenseKeyConfiguration : IEntityTypeConfiguration<LicenseKey>
{
    public void Configure(EntityTypeBuilder<LicenseKey> builder)
    {
        static string S(LicenseKeyStatus s) => $"'{SnakeCaseEnumConverter<LicenseKeyStatus>.ToProvider(s)}'";

        builder.ToTable("license_keys", InventoryModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_license_keys_status", SnakeCaseEnumConverter<LicenseKeyStatus>.CheckSql("status"));
            table.HasCheckConstraint("ck_license_keys_version", "key_version > 0");
            table.HasCheckConstraint("ck_license_keys_hash_len", $"octet_length(key_hash) = {LicenseKey.HashLength}");
            table.HasCheckConstraint("ck_license_keys_ciphertext", $"octet_length(key_encrypted) >= {LicenseKey.MinEncryptedLength}");
            table.HasCheckConstraint("ck_license_keys_hint", "char_length(key_hint) BETWEEN 1 AND 8");
            // Ràng buộc viết theo từng trạng thái: key bị thu hồi SAU khi giao vẫn giữ order_item_id để truy vết.
            table.HasCheckConstraint("ck_license_keys_available",
                $"status <> {S(LicenseKeyStatus.Available)} OR (order_item_id IS NULL AND reserved_at IS NULL AND delivered_at IS NULL)");
            table.HasCheckConstraint("ck_license_keys_assigned",
                $"status NOT IN ({S(LicenseKeyStatus.Reserved)}, {S(LicenseKeyStatus.Delivered)}) OR (order_item_id IS NOT NULL AND reserved_at IS NOT NULL)");
            table.HasCheckConstraint("ck_license_keys_delivered", $"status <> {S(LicenseKeyStatus.Delivered)} OR delivered_at IS NOT NULL");
            table.HasCheckConstraint("ck_license_keys_revoked", $"(status = {S(LicenseKeyStatus.Revoked)}) = (revoked_reason IS NOT NULL)");
        });

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.KeyEncrypted).IsRequired();
        builder.Property(k => k.KeyHash).IsRequired();
        builder.Property(k => k.KeyHint).IsRequired();
        builder.Property(k => k.Status).HasConversion(new SnakeCaseEnumConverter<LicenseKeyStatus>());
        builder.Property(k => k.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(k => k.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(k => k.VariantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportBatch>().WithMany().HasForeignKey(k => k.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OrderItem>().WithMany().HasForeignKey(k => k.OrderItemId).OnDelete(DeleteBehavior.Restrict);

        // Chống nhập trùng key (kể cả khác lô, khác gói) mà không cần giải mã.
        builder.HasIndex(k => k.KeyHash).IsUnique().HasDatabaseName("ux_license_keys_key_hash");
        // Cấp phát FIFO + đếm tồn kho — index chỉ chứa key còn trong kho.
        builder.HasIndex(k => new { k.VariantId, k.CreatedAt }).HasDatabaseName("ix_license_keys_available")
            .HasFilter($"status = {S(LicenseKeyStatus.Available)}");
        // Key của một dòng đơn.
        builder.HasIndex(k => k.OrderItemId).HasDatabaseName("ix_license_keys_order_item").HasFilter("order_item_id IS NOT NULL");
        builder.HasIndex(k => k.BatchId).HasDatabaseName("ix_license_keys_batch_id");
    }
}
