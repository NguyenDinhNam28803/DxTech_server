using System.Net;
using server.Shared.Ids;

namespace server.Modules.Payments.Domain;

/// <summary>
/// "Hộp thư đến" webhook (bảng <c>payments.webhook_events</c>, ADR-013 §3): lưu MỌI lần nhà cung cấp gọi tới, kể cả sai
/// chữ ký, TRƯỚC khi xử lý ⇒ API lỗi giữa chừng không mất sự kiện, xử lý lại được, có bằng chứng khi tranh chấp.
/// UNIQUE (provider, event_key) chặn xử lý trùng khi nhà cung cấp gửi lại.
/// </summary>
internal sealed class WebhookEvent
{
    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private WebhookEvent()
    {
        EventKey = null!;
        Payload = null!;
    }

    public WebhookEvent(
        PaymentProvider provider, string eventKey, bool signatureValid, string payloadJson, DateTimeOffset receivedAt,
        IPAddress? remoteIp = null, string? headersJson = null)
    {
        Id = UuidV7.NewGuid(receivedAt);
        Provider = provider;
        EventKey = eventKey;
        SignatureValid = signatureValid;
        Payload = payloadJson;
        ReceivedAt = receivedAt;
        RemoteIp = remoteIp;
        Headers = headersJson;
    }

    public Guid Id { get; private set; }

    public PaymentProvider Provider { get; private set; }

    /// <summary>Mã giao dịch của nhà cung cấp (SePay: <c>id</c>).</summary>
    public string EventKey { get; private set; }

    /// <summary>false ⇒ chỉ lưu làm bằng chứng, không bao giờ được xử lý (CHECK trong DB).</summary>
    public bool SignatureValid { get; private set; }

    /// <summary>IP thật sau Nginx — đối chiếu danh sách IP nhà cung cấp công bố.</summary>
    public IPAddress? RemoteIp { get; private set; }

    public string Payload { get; private set; }

    /// <summary>Chỉ header không nhạy cảm (đã bỏ chữ ký, Authorization).</summary>
    public string? Headers { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
