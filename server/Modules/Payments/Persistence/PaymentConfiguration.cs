using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Identity.Domain;
using server.Modules.Payments.Domain;
using server.Modules.Sales.Domain;

namespace server.Modules.Payments.Persistence;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        static string M(PaymentMatchStatus s) => $"'{SnakeCaseEnumConverter<PaymentMatchStatus>.ToProvider(s)}'";

        builder.ToTable("payments", PaymentsModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_payments_provider", SnakeCaseEnumConverter<PaymentProvider>.CheckSql("provider"));
            table.HasCheckConstraint("ck_payments_amount", "amount_vnd > 0");
            table.HasCheckConstraint("ck_payments_match_status", SnakeCaseEnumConverter<PaymentMatchStatus>.CheckSql("match_status"));
            // Chỉ hai trường hợp "không tìm được đơn" mới được thiếu order_id.
            table.HasCheckConstraint("ck_payments_order_link",
                $"(order_id IS NULL) = (match_status IN ({M(PaymentMatchStatus.NoOrderCode)}, {M(PaymentMatchStatus.UnknownOrder)}))");
            table.HasCheckConstraint("ck_payments_resolved", "(resolved_at IS NULL) = (resolved_by IS NULL)");
            table.HasCheckConstraint("ck_payments_texts",
                "char_length(provider_txn_id) BETWEEN 1 AND 200 AND char_length(transfer_content) <= 500 " +
                "AND (bank_reference IS NULL OR char_length(bank_reference) <= 100) " +
                "AND (resolution_note IS NULL OR char_length(resolution_note) <= 1000)");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Provider).HasConversion(new SnakeCaseEnumConverter<PaymentProvider>());
        builder.Property(p => p.MatchStatus).HasConversion(new SnakeCaseEnumConverter<PaymentMatchStatus>());
        builder.Property(p => p.ProviderTxnId).IsRequired();
        builder.Property(p => p.TransferContent).IsRequired();
        builder.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

        // FK chéo schema (cùng DB — ADR-002), không navigation.
        builder.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.ResolvedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WebhookEvent>().WithMany().HasForeignKey(p => p.WebhookEventId).OnDelete(DeleteBehavior.Restrict);

        // Chốt idempotency: webhook gửi lại bao nhiêu lần cũng chỉ một dòng.
        builder.HasIndex(p => new { p.Provider, p.ProviderTxnId }).IsUnique().HasDatabaseName("ux_payments_provider_txn");
        // Một sự kiện webhook sinh tối đa một payment.
        builder.HasIndex(p => p.WebhookEventId).IsUnique().HasDatabaseName("ux_payments_webhook_event_id")
            .HasFilter("webhook_event_id IS NOT NULL");
        builder.HasIndex(p => p.OrderId).HasDatabaseName("ix_payments_order_id").HasFilter("order_id IS NOT NULL");
        // Hàng chờ đối soát của admin.
        builder.HasIndex(p => p.CreatedAt).HasDatabaseName("ix_payments_unresolved")
            .HasFilter($"match_status <> {M(PaymentMatchStatus.Matched)} AND resolved_at IS NULL");
        builder.HasIndex(p => p.ResolvedBy).HasDatabaseName("ix_payments_resolved_by").HasFilter("resolved_by IS NOT NULL");
    }
}
