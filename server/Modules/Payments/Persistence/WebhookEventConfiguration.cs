using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Payments.Domain;

namespace server.Modules.Payments.Persistence;

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events", PaymentsModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_webhook_events_provider", SnakeCaseEnumConverter<PaymentProvider>.CheckSql("provider"));
            table.HasCheckConstraint("ck_webhook_events_event_key", "char_length(event_key) BETWEEN 1 AND 200");
            table.HasCheckConstraint("ck_webhook_events_attempts", "attempts >= 0");
            table.HasCheckConstraint("ck_webhook_events_payload", "jsonb_typeof(payload) IN ('object', 'array')");
            table.HasCheckConstraint("ck_webhook_events_headers", "headers IS NULL OR jsonb_typeof(headers) = 'object'");
            // Sự kiện sai chữ ký không bao giờ được đánh dấu đã xử lý.
            table.HasCheckConstraint("ck_webhook_events_processed", "processed_at IS NULL OR signature_valid");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Provider).HasConversion(new SnakeCaseEnumConverter<PaymentProvider>());
        builder.Property(e => e.EventKey).IsRequired();
        builder.Property(e => e.RemoteIp).HasColumnType("inet");
        builder.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.Headers).HasColumnType("jsonb");
        builder.Property(e => e.ReceivedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.Attempts).HasDefaultValue(0);

        // Sự kiện trùng (nhà cung cấp gửi lại) bị chặn ngay khi ghi.
        builder.HasIndex(e => new { e.Provider, e.EventKey }).IsUnique().HasDatabaseName("ux_webhook_events_provider_key");
        // Job xử lý lại mỗi phút — chỉ chứa sự kiện hợp lệ chưa xử lý.
        builder.HasIndex(e => e.ReceivedAt, "ix_webhook_events_unprocessed")
            .HasFilter("processed_at IS NULL AND signature_valid");
        // Dọn sự kiện > 180 ngày; đếm sự kiện sai chữ ký trong ngày (giới hạn 1.000 dòng/ngày chống làm đầy DB).
        builder.HasIndex(e => e.ReceivedAt, "ix_webhook_events_received_at");
    }
}
