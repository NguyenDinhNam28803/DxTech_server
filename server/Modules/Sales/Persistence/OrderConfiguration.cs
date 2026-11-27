using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Identity.Domain;
using server.Modules.Sales.Domain;

namespace server.Modules.Sales.Persistence;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        string pending = SalesSql.Status(OrderStatus.PendingPayment);
        string paid = SalesSql.Status(OrderStatus.Paid);
        string fulfilled = SalesSql.Status(OrderStatus.Fulfilled);
        string cancelled = SalesSql.Status(OrderStatus.Cancelled);
        string refunded = SalesSql.Status(OrderStatus.Refunded);

        builder.ToTable("orders", SalesModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_orders_code", $"code ~ '{OrderCode.Pattern}'");
            table.HasCheckConstraint("ck_orders_status", SalesSql.StatusCheck("status"));
            // Công thức tiền nằm ở DB: không đường ghi nào (code lỗi, SQL tay) tạo được đơn có tổng sai hoặc 0 đồng.
            table.HasCheckConstraint("ck_orders_amounts",
                "subtotal_vnd >= 0 AND discount_vnd >= 0 AND discount_vnd <= subtotal_vnd " +
                "AND total_vnd = subtotal_vnd - discount_vnd AND total_vnd > 0");
            table.HasCheckConstraint("ck_orders_expires", "expires_at > created_at");
            table.HasCheckConstraint("ck_orders_paid_at", $"status NOT IN ({paid}, {fulfilled}, {refunded}) OR paid_at IS NOT NULL");
            table.HasCheckConstraint("ck_orders_fulfilled_at", $"status <> {fulfilled} OR fulfilled_at IS NOT NULL");
            // Đơn mở lại khi tiền đến muộn (cancelled → paid) phải xoá thông tin huỷ; lịch sử vẫn giữ lần huỷ.
            table.HasCheckConstraint("ck_orders_cancelled",
                $"(status = {cancelled}) = (cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL)");
            table.HasCheckConstraint("ck_orders_refunded_at", $"(status = {refunded}) = (refunded_at IS NOT NULL)");
            table.HasCheckConstraint("ck_orders_cancel_reason",
                "cancel_reason IS NULL OR " + SnakeCaseEnumConverter<OrderCancelReason>.CheckSql("cancel_reason"));
            table.HasCheckConstraint("ck_orders_customer_name", "char_length(customer_name) BETWEEN 1 AND 100");
            table.HasCheckConstraint("ck_orders_customer_email", "char_length(customer_email) BETWEEN 3 AND 254");
            table.HasCheckConstraint("ck_orders_note", "note IS NULL OR char_length(note) <= 1000");
            table.HasCheckConstraint("ck_orders_vat_info", "vat_info IS NULL OR jsonb_typeof(vat_info) = 'object'");
        });

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.Code).IsRequired();
        builder.Property(o => o.Status).HasConversion(new SnakeCaseEnumConverter<OrderStatus>());
        builder.Property(o => o.CancelReason).HasConversion(new SnakeCaseEnumConverter<OrderCancelReason>());
        builder.Property(o => o.CustomerEmail).IsRequired().HasColumnType("citext");
        builder.Property(o => o.CustomerName).IsRequired();
        builder.Property(o => o.VatInfo).HasColumnType("jsonb");
        builder.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(o => o.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(o => o.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

        // FK chéo schema sang identity.users (cùng DB — ADR-002). Không có navigation: Sales không dùng kiểu của Identity.
        builder.HasOne<User>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Tra đơn theo mã: khách, hỗ trợ, nội dung chuyển khoản trong webhook.
        builder.HasIndex(o => o.Code).IsUnique().HasDatabaseName("ux_orders_code");
        // Idempotency khi tạo đơn; cũng thay index FK mặc định trên user_id.
        builder.HasIndex(o => new { o.UserId, o.IdempotencyKey }).IsUnique().HasDatabaseName("ux_orders_user_idempotency");
        // "Đơn của tôi" và đếm đơn đang chờ của một người (≤ 3).
        builder.HasIndex(o => new { o.UserId, o.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_orders_user_created");
        // Job hết hạn mỗi phút — index chỉ chứa đơn đang chờ nên rất nhỏ.
        builder.HasIndex(o => o.ExpiresAt).HasDatabaseName("ix_orders_pending_expires").HasFilter($"status = {pending}");
        // Danh sách admin lọc theo trạng thái, phân trang keyset.
        builder.HasIndex(o => new { o.Status, o.CreatedAt, o.Id }).IsDescending(false, true, true)
            .HasDatabaseName("ix_orders_status_created");
    }
}
