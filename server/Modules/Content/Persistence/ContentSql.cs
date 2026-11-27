namespace server.Modules.Content.Persistence;

/// <summary>
/// SQL viết tay của schema <c>content</c>. Hai hàm IMMUTABLE (<c>content.f_unaccent</c>, <c>content.f_blocks_text</c>) được tạo
/// trong migration Content_Initial — module không dùng hàm của schema <c>catalog</c> (ranh giới module, ADR-001).
/// </summary>
internal static class ContentSql
{
    /// <summary>Tạo hàm (migration chép nguyên văn hằng số này).</summary>
    public const string Functions = """
        CREATE FUNCTION content.f_unaccent(input text) RETURNS text
            LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
            RETURN public.unaccent('public.unaccent'::regdictionary, input);

        -- Chữ trong khối (text của đoạn/tiêu đề, câu hỏi FAQ, nhãn CTA, chú thích ảnh, trích dẫn) cho cột tìm kiếm.
        -- jsonb_path_query (không _tz) là IMMUTABLE ⇒ dùng được trong cột generated.
        CREATE FUNCTION content.f_blocks_text(blocks jsonb) RETURNS text
            LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
            RETURN (
                SELECT coalesce(string_agg(v #>> '{}', ' '), '')
                  FROM unnest(ARRAY['strict $.**.text', 'strict $.**.question', 'strict $.**.label',
                                    'strict $.**.caption', 'strict $.**.cite']::jsonpath[]) AS p(path),
                       jsonb_path_query(blocks, p.path) AS v
                 WHERE jsonb_typeof(v) = 'string');
        """;

    public const string DropFunctions = """
        DROP FUNCTION IF EXISTS content.f_blocks_text(jsonb);
        DROP FUNCTION IF EXISTS content.f_unaccent(text);
        """;

    /// <summary>Cột <c>posts.search_vector</c>: tiêu đề (A) &gt; tóm tắt (B) &gt; chữ trong khối (C), không dấu, cấu hình <c>simple</c>.</summary>
    public const string PostSearchVector = """
        setweight(to_tsvector('simple'::regconfig, content.f_unaccent(coalesce(title, ''))), 'A') || setweight(to_tsvector('simple'::regconfig, content.f_unaccent(coalesce(excerpt, ''))), 'B') || setweight(to_tsvector('simple'::regconfig, content.f_unaccent(content.f_blocks_text(blocks))), 'C')
        """;

    public static string SlugCheck(string column) => $"{column} ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length({column}) <= 150";

    public static string LengthCheck(string column, int max) => $"char_length({column}) BETWEEN 1 AND {max}";

    public static string OptionalLength(string column, int max) => $"{column} IS NULL OR char_length({column}) <= {max}";

    public static string HttpsUrl(string column) => $"{column} IS NULL OR ({column} ~ '^https://[^\\s]+$' AND char_length({column}) <= 2000)";

    /// <summary>Ghép nhiều điều kiện thành một CHECK: <c>(a) AND (b)</c>.</summary>
    public static string All(params string[] conditions) => string.Join(" AND ", conditions.Select(c => $"({c})"));

    public const string NotDeleted = "deleted_at IS NULL";
}
