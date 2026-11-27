namespace server.Modules.Payments.Features.Sepay;

/// <summary>
/// Webhook SePay (ADR-013 §3, §6). <c>WebhookSecret</c> là bí mật — chỉ trong <c>.env</c> (<c>PAYMENTS_SEPAY_WEBHOOK_SECRET</c>).
/// Để trống ⇒ endpoint webhook trả 404 (chưa bật), phần còn lại của API chạy bình thường.
/// </summary>
internal sealed class SepayOptions
{
    public const string SectionName = "Payments:Sepay";

    public string WebhookSecret { get; set; } = "";

    /// <summary>Lệch tối đa giữa <c>X-SePay-Timestamp</c> và đồng hồ server (chống phát lại).</summary>
    public int TimestampToleranceSeconds { get; set; } = 300;

    /// <summary>
    /// IP nguồn được phép (SePay công bố). Rỗng ⇒ không kiểm IP (chỉ dựa vào chữ ký). IP lấy sau Nginx (forwarded headers).
    /// </summary>
    public List<string> AllowedIps { get; set; } = [];

    /// <summary>Tắt kiểm IP (chỉ dùng cho test / khi SePay đổi IP chưa kịp cập nhật). Chữ ký vẫn luôn bắt buộc.</summary>
    public bool CheckIps { get; set; } = true;

    /// <summary>Số sự kiện sai chữ ký tối đa được lưu mỗi ngày (bằng chứng) — chống làm đầy DB.</summary>
    public int MaxInvalidEventsPerDay { get; set; } = 1000;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(WebhookSecret);
}
