using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace server.Infrastructure.Persistence.Conversions;

/// <summary>
/// <c>List&lt;string&gt;</c> ↔ cột <c>jsonb</c> dạng mảng (<c>["a","b"]</c>), dùng cho dữ liệu chỉ để hiển thị
/// (quyết định D10). Dùng System.Text.Json có sẵn, không bật dynamic JSON của Npgsql.
/// Kèm <see cref="Comparer"/> để EF phát hiện thay đổi bên trong danh sách (thêm/xoá phần tử).
/// </summary>
public static class JsonListConverter
{
    public static readonly ValueConverter<List<string>, string> Converter = new(
        list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<List<string>>(json, (JsonSerializerOptions?)null) ?? new List<string>());

    public static readonly ValueComparer<List<string>> Comparer = new(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
        list => list.ToList());
}
