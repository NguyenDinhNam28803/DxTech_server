namespace server.Modules.Catalog.Persistence;

/// <summary>
/// SQL viết tay dùng trong cấu hình EF của schema <c>catalog</c>.
/// Migration KHÔNG tham chiếu các hằng số này mà chép nguyên văn, để migration đã merge không đổi theo code.
/// Hàm <c>catalog.f_unaccent</c> (bọc <c>unaccent()</c> thành IMMUTABLE) được tạo trong migration Catalog_Initial.
/// </summary>
internal static class CatalogSql
{
    /// <summary>
    /// Cột <c>products.search_vector</c> (generated, stored). Cấu hình <c>simple</c>: không stemming tiếng Anh,
    /// chỉ hạ chữ thường — hợp với tiếng Việt đã bỏ dấu. Trọng số: tên (A) &gt; hãng (B) &gt; mô tả ngắn (C),
    /// để PR-4 xếp hạng bằng <c>ts_rank</c>.
    /// </summary>
    public const string ProductSearchVector = """
        setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(name, ''))), 'A') || setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(brand, ''))), 'B') || setweight(to_tsvector('simple'::regconfig, catalog.f_unaccent(coalesce(short_description, ''))), 'C')
        """;

    /// <summary>Slug dùng trong URL: chữ thường không dấu, số, gạch ngang; không bắt đầu/kết thúc bằng gạch ngang.</summary>
    public static string SlugCheck(string column) => $"{column} ~ '^[a-z0-9]+(-[a-z0-9]+)*$'";

    public static string LengthCheck(string column, int max) => $"char_length({column}) BETWEEN 1 AND {max}";

    public const string NotDeleted = "deleted_at IS NULL";

    // Về HasDefaultValue(x).HasSentinel(x) trong các cấu hình: EF Core bỏ cột khỏi INSERT khi giá trị bằng
    // "sentinel" (mặc định là default của CLR: false/0) để database tự điền DEFAULT. Nếu để sentinel mặc định,
    // IsActive = false sẽ bị bỏ qua và DB ghi true. Đặt sentinel = chính giá trị DEFAULT ⇒ chỉ bỏ cột khi
    // kết quả giống hệt nhau; mọi giá trị khác (false, 0) được gửi tường minh.
}
