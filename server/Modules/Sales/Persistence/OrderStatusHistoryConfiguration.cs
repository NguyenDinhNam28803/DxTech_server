using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Sales.Domain;

namespace server.Modules.Sales.Persistence;

internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        builder.ToTable("order_status_history", SalesModule.Schema, table =>
        {
            table.HasCheckConstraint("ck_order_status_history_statuses",
                SalesSql.StatusCheck("to_status") + " AND (from_status IS NULL OR " + SalesSql.StatusCheck("from_status") + ")");
            table.HasCheckConstraint("ck_order_status_history_transition", "from_status IS DISTINCT FROM to_status");
            table.HasCheckConstraint("ck_order_status_history_actor_type", SnakeCaseEnumConverter<OrderActorType>.CheckSql("actor_type"));
            table.HasCheckConstraint("ck_order_status_history_reason", "reason IS NULL OR char_length(reason) <= 200");
        });

        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).UseIdentityAlwaysColumn();
        builder.Property(h => h.FromStatus).HasConversion(new SnakeCaseEnumConverter<OrderStatus>());
        builder.Property(h => h.ToStatus).HasConversion(new SnakeCaseEnumConverter<OrderStatus>());
        builder.Property(h => h.ActorType).HasConversion(new SnakeCaseEnumConverter<OrderActorType>());
        builder.Property(h => h.CreatedAt).HasDefaultValueSql("now()");

        builder.HasOne<Order>().WithMany().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Restrict);

        // Dòng thời gian của một đơn theo thứ tự; thay index FK mặc định.
        builder.HasIndex(h => new { h.OrderId, h.Id }).HasDatabaseName("ix_order_status_history_order_id");
    }
}
