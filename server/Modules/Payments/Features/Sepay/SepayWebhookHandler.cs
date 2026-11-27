using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using server.Infrastructure.Persistence;
using server.Modules.Payments.Domain;

namespace server.Modules.Payments.Features.Sepay;

public enum WebhookOutcome
{
    /// <summary>Chưa cấu hình secret ⇒ 404.</summary>
    NotEnabled,
    /// <summary>Sai chữ ký / timestamp / IP ⇒ 401 (đã lưu làm bằng chứng nếu chưa vượt hạn mức).</summary>
    Rejected,
    /// <summary>Body không phải JSON object ⇒ 400.</summary>
    BadRequest,
    /// <summary>Đã nhận (mới hoặc trùng) và xử lý xong ⇒ 200 <c>{"success": true}</c>.</summary>
    Accepted,
    /// <summary>Đã lưu nhưng xử lý lỗi ⇒ 500: SePay gửi lại, job cũng xử lý lại.</summary>
    Failed,
}

/// <summary>
/// Nhận webhook SePay (ADR-013 §3): kiểm IP + chữ ký trên raw body + timestamp → ghi hộp thư TRƯỚC → xử lý.
/// Trùng (SePay gửi lại) ⇒ trả thành công ngay, không xử lý hai lần (UNIQUE provider + event_key).
/// </summary>
public sealed class SepayWebhookHandler
{
    private readonly AppDbContext _db;
    private readonly WebhookProcessor _processor;
    private readonly SepayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SepayWebhookHandler> _logger;

    internal SepayWebhookHandler(AppDbContext db, WebhookProcessor processor, IOptions<SepayOptions> options, TimeProvider time, ILogger<SepayWebhookHandler> logger)
    {
        _db = db;
        _processor = processor;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<WebhookOutcome> HandleAsync(byte[] body, string? signature, string? timestamp, IPAddress? remoteIp, string? userAgent, CancellationToken ct)
    {
        if (!_options.IsEnabled)
        {
            return WebhookOutcome.NotEnabled;
        }

        DateTimeOffset now = _time.GetUtcNow();
        string? rejection = IpAllowed(remoteIp)
            ? SepaySignature.Verify(_options.WebhookSecret, signature, timestamp, body, now, _options.TimestampToleranceSeconds)
            : "ip_not_allowed";

        string json = Encoding.UTF8.GetString(body);
        bool isJsonObject = IsJsonObject(json);
        string headers = JsonSerializer.Serialize(new { timestamp, userAgent });

        if (rejection is not null)
        {
            await StoreRejectedAsync(body, isJsonObject ? json : "{}", headers, remoteIp, rejection, now, ct);
            _logger.LogWarning("SePay webhook rejected ({Reason}) from {Ip}", rejection, remoteIp);
            return WebhookOutcome.Rejected;
        }

        SepayPayload? payload = isJsonObject ? SepayPayload.TryParse(json) : null;
        if (payload is null)
        {
            return WebhookOutcome.BadRequest;
        }

        // SePay gửi lại là chuyện thường (timeout, 5xx): kiểm trước để không ghi lỗi UNIQUE vào log. UNIQUE vẫn chặn khi đua.
        if (await _db.Set<WebhookEvent>().AnyAsync(e => e.Provider == PaymentProvider.Sepay && e.EventKey == payload.Id, ct))
        {
            return WebhookOutcome.Accepted;
        }

        var evt = new WebhookEvent(PaymentProvider.Sepay, payload.Id, signatureValid: true, json, now, remoteIp, headers);
        _db.Add(evt);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // SePay gửi lại sự kiện đã nhận: không xử lý lần hai. Nếu lần trước chưa xử lý xong, job sẽ làm.
            return WebhookOutcome.Accepted;
        }

        try
        {
            await _processor.ProcessAsync(evt.Id, ct);
            return WebhookOutcome.Accepted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SePay webhook {EventId} failed — will be retried", evt.Id);
            _db.ChangeTracker.Clear();
            await _processor.RecordFailureAsync(evt.Id, ex, ct);
            return WebhookOutcome.Failed;
        }
    }

    private bool IpAllowed(IPAddress? ip)
        => !_options.CheckIps
           || _options.AllowedIps.All(string.IsNullOrWhiteSpace)
           || (ip is not null && _options.AllowedIps.Any(a => IPAddress.TryParse(a, out IPAddress? allowed)
               && allowed.Equals(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)));

    private async Task StoreRejectedAsync(byte[] body, string json, string headers, IPAddress? ip, string reason, DateTimeOffset now, CancellationToken ct)
    {
        DateTimeOffset dayStart = new(now.UtcDateTime.Date, TimeSpan.Zero);
        int today = await _db.Set<WebhookEvent>().CountAsync(e => !e.SignatureValid && e.ReceivedAt >= dayStart, ct);
        if (today >= _options.MaxInvalidEventsPerDay)
        {
            return; // chống làm đầy DB: chỉ log
        }

        // event_key theo nội dung: cùng một body giả gửi nhiều lần chỉ lưu một dòng.
        string key = "invalid:" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (await _db.Set<WebhookEvent>().AnyAsync(e => e.Provider == PaymentProvider.Sepay && e.EventKey == key, ct))
        {
            return;
        }

        var evt = new WebhookEvent(PaymentProvider.Sepay, key, signatureValid: false, json, now, ip, headers) { LastError = reason };
        _db.Add(evt);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
        }
    }

    private static bool IsJsonObject(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
