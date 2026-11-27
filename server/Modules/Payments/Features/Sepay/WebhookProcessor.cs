using Microsoft.EntityFrameworkCore;
using Npgsql;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Payments.Domain;
using server.Modules.Sales.Contracts;

namespace server.Modules.Payments.Features.Sepay;

/// <summary>
/// Xử lý một sự kiện webhook đã lưu (ADR-013 §3 bước 3): một transaction — khoá sự kiện, áp tiền vào đơn (Sales),
/// ghi payment, đánh dấu đã xử lý. Gọi đồng bộ từ request webhook và từ job xử lý lại. Gọi lại nhiều lần an toàn.
/// </summary>
internal sealed class WebhookProcessor(AppDbContext db, IOrderPayments orders, TimeProvider time)
{
    /// <returns>true nếu đã xử lý (lần này hoặc trước đó).</returns>
    public async Task<bool> ProcessAsync(Guid eventId, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        List<Guid> locked = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM payments.webhook_events
             WHERE id = {eventId} AND signature_valid AND processed_at IS NULL
             FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (locked.Count == 0)
        {
            return true; // đã xử lý, sai chữ ký, hoặc một tiến trình khác đang xử lý đúng sự kiện này
        }

        WebhookEvent evt = await db.Set<WebhookEvent>().SingleAsync(e => e.Id == eventId, ct);
        SepayPayload? p = SepayPayload.TryParse(evt.Payload);
        evt.Attempts++;
        evt.ProcessedAt = now;

        if (p is { IsIncoming: true, AmountVnd: > 0 })
        {
            PaymentApplication applied = await orders.ApplyAsync(p.Content, p.AmountVnd, now, ct);
            db.Add(new Payment(PaymentProvider.Sepay, p.Id, p.Content, p.AmountVnd, p.TransactionDate ?? now,
                applied.OrderId, SnakeCaseEnumConverter<PaymentMatchStatus>.FromProvider(applied.MatchStatus),
                now, evt.Id, Truncate(p.ReferenceCode, 100)));
        }
        else
        {
            // Tiền ra / số tiền 0 / body không đọc được: chỉ lưu làm bằng chứng, không ghi payment.
            evt.LastError = p is null ? "unparsable_payload" : "ignored_not_incoming";
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_payments_provider_txn" })
        {
            // Cùng giao dịch đã được ghi qua một sự kiện khác (không nên xảy ra vì event_key = id giao dịch) ⇒ coi như đã xử lý.
            return true;
        }

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Ghi lần thất bại (ngoài transaction đã rollback) để job xử lý lại và admin thấy lỗi.</summary>
    public async Task RecordFailureAsync(Guid eventId, Exception ex, CancellationToken ct)
    {
        string error = Truncate($"{ex.GetType().Name}: {ex.GetBaseException().Message}", 1000)!;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE payments.webhook_events SET attempts = attempts + 1, last_error = {error}
             WHERE id = {eventId} AND processed_at IS NULL
            """, ct);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
