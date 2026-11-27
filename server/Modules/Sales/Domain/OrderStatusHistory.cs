namespace server.Modules.Sales.Domain;

/// <summary>
/// Một lần đơn đổi trạng thái (bảng <c>sales.order_status_history</c>) — chỉ thêm, không sửa/xoá (quyền DB chặn).
/// Dòng thời gian cho khách và admin, bằng chứng khi đối soát. Khác audit log: ghi cả việc hệ thống làm (webhook, job).
/// </summary>
internal sealed class OrderStatusHistory
{
    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private OrderStatusHistory()
    {
    }

    public OrderStatusHistory(
        Guid orderId, OrderStatus? fromStatus, OrderStatus toStatus, OrderActorType actorType, DateTimeOffset createdAt,
        Guid? actorUserId = null, string? reason = null)
    {
        OrderId = orderId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ActorType = actorType;
        ActorUserId = actorUserId;
        Reason = reason;
        CreatedAt = createdAt;
    }

    /// <summary>bigint IDENTITY — thứ tự sự kiện tuyệt đối.</summary>
    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>null khi tạo đơn.</summary>
    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }

    public OrderActorType ActorType { get; private set; }

    /// <summary>Không FK: lịch sử phải sống lâu hơn tài khoản (ẩn danh hoá theo NĐ 13).</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>Ví dụ: expired, late_payment, customer_request.</summary>
    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
