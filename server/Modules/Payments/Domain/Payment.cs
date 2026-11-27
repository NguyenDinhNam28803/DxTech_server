using server.Shared.Ids;

namespace server.Modules.Payments.Domain;

/// <summary>
/// Một khoản tiền vào đã được nhà cung cấp xác nhận (bảng <c>payments.payments</c>) — chứng từ, chỉ thêm.
/// UNIQUE (provider, provider_txn_id) là chốt idempotency thứ hai sau hộp thư webhook.
/// Ứng dụng chỉ được cập nhật 3 cột xử lý khoản lệch (<c>resolved_*</c>) — quyền DB theo cột.
/// </summary>
internal sealed class Payment
{
    /// <summary>Cho EF Core khi đọc từ database.</summary>
    private Payment()
    {
        ProviderTxnId = null!;
        TransferContent = null!;
    }

    public Payment(
        PaymentProvider provider, string providerTxnId, string transferContent, long amountVnd, DateTimeOffset paidAt,
        Guid? orderId, PaymentMatchStatus matchStatus, DateTimeOffset createdAt, Guid? webhookEventId = null, string? bankReference = null)
    {
        if (amountVnd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountVnd), amountVnd, "Số tiền phải dương.");
        }

        Id = UuidV7.NewGuid(createdAt);
        Provider = provider;
        ProviderTxnId = providerTxnId;
        TransferContent = transferContent;
        AmountVnd = amountVnd;
        PaidAt = paidAt;
        OrderId = orderId;
        MatchStatus = matchStatus;
        CreatedAt = createdAt;
        WebhookEventId = webhookEventId;
        BankReference = bankReference;
    }

    public Guid Id { get; private set; }

    /// <summary>null khi không tìm được đơn (<see cref="PaymentMatchStatus.NoOrderCode"/>, <see cref="PaymentMatchStatus.UnknownOrder"/>).</summary>
    public Guid? OrderId { get; private set; }

    public PaymentProvider Provider { get; private set; }

    public string ProviderTxnId { get; private set; }

    /// <summary>Mã tham chiếu của ngân hàng — dùng khi tra soát với ngân hàng.</summary>
    public string? BankReference { get; private set; }

    public string TransferContent { get; private set; }

    public long AmountVnd { get; private set; }

    public PaymentMatchStatus MatchStatus { get; private set; }

    public Guid? WebhookEventId { get; private set; }

    /// <summary>Thời điểm ngân hàng ghi nhận.</summary>
    public DateTimeOffset PaidAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public Guid? ResolvedBy { get; set; }

    public string? ResolutionNote { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
