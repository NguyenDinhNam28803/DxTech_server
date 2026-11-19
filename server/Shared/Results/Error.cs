namespace server.Shared.Results;

/// <summary>
/// Loại lỗi nghiệp vụ dự đoán được. Mỗi loại ứng với đúng một mã HTTP
/// (xem <c>Infrastructure/Http/ResultHttpExtensions</c>), để mọi module trả lỗi thống nhất.
/// </summary>
public enum ErrorType
{
    /// <summary>400 — dữ liệu đầu vào sai.</summary>
    Validation,
    /// <summary>401 — chưa đăng nhập.</summary>
    Unauthorized,
    /// <summary>403 — đăng nhập rồi nhưng không đủ quyền.</summary>
    Forbidden,
    /// <summary>404 — không tìm thấy (hoặc đã bị ẩn).</summary>
    NotFound,
    /// <summary>409 — xung đột: trùng khoá, ghi đè đồng thời.</summary>
    Conflict,
    /// <summary>422 — vi phạm quy tắc nghiệp vụ: hết key, coupon hết hạn, chuyển trạng thái sai.</summary>
    BusinessRule,
}

/// <summary>
/// Một lỗi nghiệp vụ.
/// <paramref name="Code"/> là mã ổn định cho client xử lý (ví dụ <c>catalog.product_not_found</c>);
/// <paramref name="Message"/> là thông báo cho người dùng.
/// </summary>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null)
{
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        => new(code, message, ErrorType.Validation, fieldErrors);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error BusinessRule(string code, string message) => new(code, message, ErrorType.BusinessRule);
}
