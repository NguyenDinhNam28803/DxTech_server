using System.Net.Mail;

namespace server.Modules.Identity.Domain;

/// <summary>Kiểm tra và chuẩn hoá email trước khi lưu. So sánh hoa thường do cột <c>citext</c> lo.</summary>
internal static class EmailAddress
{
    public const int MaxLength = 254;

    /// <summary>Bỏ khoảng trắng hai đầu, hạ chữ thường; trả null nếu không phải một địa chỉ email đơn hợp lệ.</summary>
    public static string? Normalize(string? input)
    {
        string email = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length is < 3 or > MaxLength || email.Any(char.IsWhiteSpace))
        {
            return null;
        }

        // MailAddress chấp nhận cả dạng "Tên <a@b.c>" ⇒ yêu cầu kết quả trùng đúng chuỗi đầu vào.
        if (!MailAddress.TryCreate(email, out MailAddress? parsed) || parsed.Address != email)
        {
            return null;
        }

        int at = email.LastIndexOf('@');
        string domain = email[(at + 1)..];
        return at > 0 && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') ? email : null;
    }
}
