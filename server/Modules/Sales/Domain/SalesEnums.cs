namespace server.Modules.Sales.Domain;

/// <summary>
/// Trạng thái đơn (phase3-schema.md mục 2). Lưu DB: pending_payment | paid | fulfilled | cancelled | refunded.
/// Chuyển đổi hợp lệ do code PR-11/PR-12 kiểm soát; DB chặn tổ hợp mâu thuẫn (thời điểm, lý do huỷ).
/// </summary>
internal enum OrderStatus
{
    PendingPayment,
    Paid,
    Fulfilled,
    Cancelled,
    Refunded,
}

/// <summary>Lý do huỷ đơn. Lưu DB: expired | customer | admin.</summary>
internal enum OrderCancelReason
{
    Expired,
    Customer,
    Admin,
}

/// <summary>Ai làm đơn đổi trạng thái. Lưu DB: customer | admin | system | webhook.</summary>
internal enum OrderActorType
{
    Customer,
    Admin,
    System,
    Webhook,
}

/// <summary>
/// Cách giao của một dòng đơn — chụp lại từ gói lúc đặt (Catalog có enum riêng; module không dùng kiểu nội bộ của nhau).
/// Lưu DB: license_key | account | manual.
/// </summary>
internal enum ItemDeliveryType
{
    LicenseKey,
    Account,
    Manual,
}
