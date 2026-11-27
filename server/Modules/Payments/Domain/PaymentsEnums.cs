namespace server.Modules.Payments.Domain;

/// <summary>Nhà cung cấp gửi tiền/webhook (ADR-013 §1). Lưu DB: sepay | payos | manual.</summary>
internal enum PaymentProvider
{
    Sepay,
    Payos,
    /// <summary>Admin ghi nhận tay (Phase 4) — đường dự phòng.</summary>
    Manual,
}

/// <summary>
/// Kết quả đối chiếu khoản tiền với đơn (ADR-013 §4, phase3-schema.md mục 3.5).
/// Mọi giá trị khác <see cref="Matched"/> vào hàng chờ admin cho tới khi có <c>resolved_at</c>.
/// </summary>
internal enum PaymentMatchStatus
{
    /// <summary>Đúng đơn, đúng tiền ⇒ cấp key.</summary>
    Matched,
    /// <summary>Đúng đơn, thiếu tiền ⇒ không cấp key.</summary>
    Underpaid,
    /// <summary>Đúng đơn, thừa tiền ⇒ vẫn cấp key, hoàn phần thừa.</summary>
    Overpaid,
    /// <summary>Nội dung chuyển khoản không có mã đơn.</summary>
    NoOrderCode,
    /// <summary>Có mã dạng DX… nhưng không tồn tại.</summary>
    UnknownOrder,
    /// <summary>Đơn đã trả / không nhận thêm tiền (chuyển 2 lần).</summary>
    OrderNotPayable,
    /// <summary>Tiền đến sau khi đơn hết hạn và không còn đủ key để mở lại.</summary>
    LateNoStock,
}
