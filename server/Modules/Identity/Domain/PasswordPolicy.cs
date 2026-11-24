namespace server.Modules.Identity.Domain;

/// <summary>
/// Quy tắc mật khẩu theo NIST SP 800-63B (ADR-012 §2): độ dài là chính, không bắt buộc ký tự đặc biệt/hoa/số
/// (các quy tắc đó khiến người dùng chọn mật khẩu dễ đoán theo mẫu "Abc@1234"), chặn mật khẩu quá phổ biến và
/// mật khẩu chứa email. Danh sách mật khẩu phổ biến đầy đủ (10.000) bổ sung ở PR-8 cùng API đăng ký.
/// </summary>
internal static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    // Mẫu tối thiểu cho PR-7 (lệnh tạo admin). PR-8 thay bằng danh sách 10.000 mật khẩu phổ biến nhúng trong assembly.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "1234567890", "12345678910", "123456789a", "0123456789", "1111111111", "0000000000",
        "qwertyuiop", "password123", "password1234", "iloveyou123", "abcdefghij", "matkhau123",
        "1q2w3e4r5t", "qwerty12345", "admin12345", "administrator", "dxtechs123", "dxtechs2026",
    };

    /// <summary>Trả về danh sách lỗi (tiếng Việt); rỗng = hợp lệ.</summary>
    public static IReadOnlyList<string> Validate(string? password, string? email)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Mật khẩu không được để trống.");
            return errors;
        }

        // Đếm theo ký tự hiển thị (text element) để mật khẩu tiếng Việt có dấu không bị tính dài hơn thực tế.
        int length = new System.Globalization.StringInfo(password).LengthInTextElements;
        if (length < MinLength)
        {
            errors.Add($"Mật khẩu phải có ít nhất {MinLength} ký tự.");
        }

        if (length > MaxLength)
        {
            errors.Add($"Mật khẩu tối đa {MaxLength} ký tự.");
        }

        if (Common.Contains(password) || password.Distinct().Count() <= 2)
        {
            errors.Add("Mật khẩu quá phổ biến hoặc dễ đoán.");
        }

        string? local = email?.Split('@')[0];
        if (!string.IsNullOrEmpty(local) && local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Mật khẩu không được chứa email.");
        }

        return errors;
    }
}
