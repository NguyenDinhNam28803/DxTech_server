using System.Globalization;
using System.Text.Json;

namespace server.Modules.Payments.Features.Sepay;

/// <summary>
/// Các trường dùng từ body webhook SePay (docs.sepay.vn — Tích hợp Webhooks):
/// <c>id</c>, <c>transferType</c> (in/out), <c>transferAmount</c>, <c>content</c>, <c>code</c>, <c>referenceCode</c>,
/// <c>transactionDate</c> ("yyyy-MM-dd HH:mm:ss", giờ Việt Nam). ⚠️ Xác nhận lại với tài khoản SePay thật (nghiệm thu 2.000 ₫).
/// </summary>
internal sealed record SepayPayload(string Id, bool IsIncoming, long AmountVnd, string Content, string? ReferenceCode, DateTimeOffset? TransactionDate)
{
    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    public static SepayPayload? TryParse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out JsonElement id))
            {
                return null;
            }

            string idText = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? "";
            if (idText.Length is 0 or > 200)
            {
                return null;
            }

            bool incoming = !r.TryGetProperty("transferType", out JsonElement t) || string.Equals(t.GetString(), "in", StringComparison.OrdinalIgnoreCase);
            long amount = r.TryGetProperty("transferAmount", out JsonElement a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out long v) ? v : 0;
            string content = Str(r, "content") ?? Str(r, "description") ?? "";
            // Một số cấu hình SePay tách sẵn mã thanh toán vào "code": ghép vào nội dung để tìm mã đơn ở một chỗ.
            if (Str(r, "code") is { Length: > 0 } code && !content.Contains(code, StringComparison.OrdinalIgnoreCase))
            {
                content = $"{code} {content}";
            }

            DateTimeOffset? at = Str(r, "transactionDate") is { } d
                && DateTime.TryParseExact(d, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local)
                ? new DateTimeOffset(local, Vietnam).ToUniversalTime()   // DB chỉ nhận UTC (ADR-006)
                : null;

            return new SepayPayload(idText, incoming, amount, content.Length > 500 ? content[..500] : content, Str(r, "referenceCode"), at);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement r, string name)
        => r.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
