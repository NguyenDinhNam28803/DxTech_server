using System.Globalization;
using System.Reflection;

namespace server.Modules.Identity.Domain;

/// <summary>
/// Quy tắc mật khẩu theo NIST SP 800-63B (ADR-012 §2): độ dài là chính, không bắt buộc ký tự đặc biệt/hoa/số
/// (các quy tắc đó khiến người dùng chọn mật khẩu dễ đoán theo mẫu "Abc@1234"), chặn mật khẩu nằm trong danh
/// sách mật khẩu phổ biến và mật khẩu chứa email.
/// </summary>
internal static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    private const string CommonPasswordsResource = "server.Modules.Identity.Domain.common-passwords.txt";

    /// <summary>~2.300 mật khẩu phổ biến dài ≥ 10 ký tự (mật khẩu ngắn hơn đã bị quy tắc độ dài chặn).</summary>
    private static readonly Lazy<HashSet<string>> Common = new(LoadCommonPasswords);

    public static int CommonPasswordCount => Common.Value.Count;

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
        int length = new StringInfo(password).LengthInTextElements;
        if (length < MinLength)
        {
            errors.Add($"Mật khẩu phải có ít nhất {MinLength} ký tự.");
        }

        if (length > MaxLength)
        {
            errors.Add($"Mật khẩu tối đa {MaxLength} ký tự.");
        }

        if (Common.Value.Contains(password) || password.Distinct().Count() <= 2)
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

    private static HashSet<string> LoadCommonPasswords()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CommonPasswordsResource)
            ?? throw new InvalidOperationException($"Thiếu resource {CommonPasswordsResource}");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                set.Add(line.Trim());
            }
        }

        return set;
    }
}
