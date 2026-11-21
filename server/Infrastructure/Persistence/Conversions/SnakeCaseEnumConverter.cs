using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using server.Infrastructure.Persistence.Conventions;

namespace server.Infrastructure.Persistence.Conversions;

/// <summary>
/// Lưu enum thành <c>text</c> dạng snake_case: <c>DeliveryType.LicenseKey</c> → <c>'license_key'</c>
/// (quyết định D6: text + CHECK — đọc DB thấy ngay giá trị, thêm giá trị chỉ cần sửa CHECK).
/// Giá trị lạ trong DB ⇒ ném lỗi khi đọc thay vì âm thầm ra giá trị mặc định.
/// </summary>
public sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => ToProvider(value),
    value => FromProvider(value))
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToDb =
        Enum.GetValues<TEnum>().ToDictionary(v => v, v => SnakeCaseNamingConvention.ToSnakeCase(v.ToString()));

    private static readonly Dictionary<string, TEnum> FromDb = ToDb.ToDictionary(p => p.Value, p => p.Key);

    /// <summary>Các giá trị hợp lệ trong DB, ví dụ <c>['license_key', 'account', 'manual']</c>.</summary>
    public static IReadOnlyCollection<string> DbValues => ToDb.Values;

    /// <summary>Biểu thức cho CHECK constraint: <c>delivery_type IN ('license_key', 'account', 'manual')</c>.</summary>
    public static string CheckSql(string column) => $"{column} IN ({string.Join(", ", ToDb.Values.Select(v => $"'{v}'"))})";

    public static string ToProvider(TEnum value)
        => ToDb.TryGetValue(value, out string? text)
            ? text
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Giá trị {typeof(TEnum).Name} không hợp lệ");

    public static TEnum FromProvider(string value)
        => FromDb.TryGetValue(value, out TEnum result)
            ? result
            : throw new InvalidOperationException($"Giá trị '{value}' trong database không thuộc {typeof(TEnum).Name}");
}
