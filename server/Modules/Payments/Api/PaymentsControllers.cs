using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Identity.Contracts;
using server.Modules.Payments.Domain;
using server.Modules.Payments.Features.Sepay;

namespace server.Modules.Payments.Api;

/// <summary>
/// Webhook của nhà cung cấp thanh toán (ADR-013 §3). Không đăng nhập — xác thực bằng chữ ký HMAC trên raw body.
/// Gọi server-to-server nên không qua CORS. Body tối đa 64 KB.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/payments/webhooks")]
public sealed class PaymentWebhooksController(SepayWebhookHandler sepay) : ControllerBase
{
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>SePay: 200 <c>{"success":true}</c> khi đã nhận (kể cả trùng); 401 sai chữ ký/timestamp/IP; 500 ⇒ SePay gửi lại.</summary>
    [HttpPost("sepay")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Sepay(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        WebhookOutcome outcome = await sepay.HandleAsync(
            buffer.ToArray(),
            Request.Headers[SepaySignature.SignatureHeader].FirstOrDefault(),
            Request.Headers[SepaySignature.TimestampHeader].FirstOrDefault(),
            HttpContext.Connection.RemoteIpAddress,
            Request.Headers.UserAgent.FirstOrDefault(),
            ct);

        return outcome switch
        {
            WebhookOutcome.Accepted => Ok(new { success = true }),
            WebhookOutcome.NotEnabled => NotFound(),
            WebhookOutcome.BadRequest => BadRequest(new { success = false }),
            WebhookOutcome.Rejected => Unauthorized(new { success = false }),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new { success = false }),
        };
    }
}

public sealed record UnresolvedPayment(
    Guid Id, string Provider, string ProviderTxnId, string? BankReference, string TransferContent, long AmountVnd,
    string MatchStatus, Guid? OrderId, DateTimeOffset PaidAt, DateTimeOffset CreatedAt);

/// <summary>Hàng chờ đối soát cho admin (chỉ đọc; xử lý khoản lệch ở Phase 4).</summary>
[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/admin/payments")]
public sealed class AdminPaymentsController(AppDbContext db) : ControllerBase
{
    /// <summary>Khoản tiền lệch chưa xử lý (thiếu/thừa tiền, không mã đơn, mã lạ, đơn không nhận tiền, đến muộn hết key), cũ nhất trước.</summary>
    [HttpGet("unresolved")]
    [ProducesResponseType(typeof(IReadOnlyList<UnresolvedPayment>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unresolved(CancellationToken ct)
    {
        var rows = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.MatchStatus != PaymentMatchStatus.Matched && p.ResolvedAt == null)
            .OrderBy(p => p.CreatedAt).Take(200)
            .ToListAsync(ct);
        return Ok(rows.Select(p => new UnresolvedPayment(
            p.Id, SnakeCaseEnumConverter<PaymentProvider>.ToProvider(p.Provider), p.ProviderTxnId, p.BankReference, p.TransferContent,
            p.AmountVnd, SnakeCaseEnumConverter<PaymentMatchStatus>.ToProvider(p.MatchStatus), p.OrderId, p.PaidAt, p.CreatedAt)).ToList());
    }
}
