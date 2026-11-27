using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace server.Modules.Content.Domain;

/// <summary>Kết quả kiểm khối: lỗi (đường dẫn JSON + lý do), ảnh được tham chiếu, chữ thuần (đếm phút đọc, nguồn cho AI).</summary>
internal sealed record BlocksCheck(IReadOnlyList<string> Errors, IReadOnlyCollection<Guid> MediaIds, string PlainText)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Kiểm thân bài dạng khối (ADR-015 §3, thiết kế CT mục 2) — danh sách trắng, từ chối thay vì bỏ qua:
/// loại khối lạ, trường lạ, sai kiểu, quá dài, <c>id</c> trùng, liên kết không phải đường dẫn nội bộ hoặc <c>https://</c>.
/// Không có chỗ nào chứa HTML ⇒ web chỉ in chữ (Vue tự escape) — chống XSS từ gốc.
/// Ảnh: chỉ kiểm dạng UUID ở đây; tồn tại/chưa xoá do nơi lưu kiểm (cần DB).
/// </summary>
internal static partial class ContentBlocks
{
    public const int MaxBlocks = 200;
    public const int MaxTotalChars = 50_000;
    public const int MaxSpanChars = 5_000;
    public const int MaxSpans = 100;
    public const int MaxListItems = 100;
    public const int MaxHrefLength = 2_000;

    private static readonly Dictionary<string, string[]> Fields = new()
    {
        ["heading"] = ["level", "text"],
        ["paragraph"] = ["spans"],
        ["list"] = ["ordered", "items"],
        ["image"] = ["mediaId", "alt", "caption"],
        ["callout"] = ["tone", "spans"],
        ["quote"] = ["spans", "cite"],
        ["cta"] = ["label", "href"],
        ["faq"] = ["question", "answer"],
    };

    private static readonly string[] SpanFields = ["text", "bold", "italic", "href"];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex BlockId();

    public static BlocksCheck Validate(string json)
    {
        var errors = new List<string>();
        var media = new HashSet<Guid>();
        var text = new StringBuilder();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            Check(doc.RootElement, errors, media, text);
        }
        catch (JsonException ex)
        {
            errors.Add($"$: JSON không hợp lệ ({ex.Message})");
        }

        if (text.Length > MaxTotalChars)
        {
            errors.Add($"$: tổng chữ {text.Length} vượt {MaxTotalChars} ký tự");
        }

        return new BlocksCheck(errors, media, text.ToString().Trim());
    }

    /// <summary>Phút đọc ước lượng (~200 từ/phút), tối thiểu 1.</summary>
    public static short ReadingMinutes(string plainText)
    {
        int words = plainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return (short)Math.Max(1, (int)Math.Ceiling(words / 200.0));
    }

    /// <summary>Liên kết hợp lệ: đường dẫn nội bộ <c>/…</c> (không phải <c>//host</c>) hoặc URL <c>https://</c> tuyệt đối.</summary>
    public static bool IsSafeHref(string href)
    {
        if (href.Length is 0 or > MaxHrefLength || href.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
        {
            return false;
        }

        if (href[0] == '/')
        {
            return href.Length == 1 || href[1] != '/';
        }

        return Uri.TryCreate(href, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static void Check(JsonElement root, List<string> errors, HashSet<Guid> media, StringBuilder text)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            errors.Add("$: phải là mảng khối");
            return;
        }

        if (root.GetArrayLength() > MaxBlocks)
        {
            errors.Add($"$: tối đa {MaxBlocks} khối");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement block in root.EnumerateArray())
        {
            string path = $"$[{index++}]";
            if (block.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: khối phải là object");
                continue;
            }

            string? type = Str(block, "type");
            if (type is null || !Fields.TryGetValue(type, out string[]? allowed))
            {
                errors.Add($"{path}.type: loại khối không được hỗ trợ");
                continue;
            }

            string? id = Str(block, "id");
            if (id is null || !BlockId().IsMatch(id))
            {
                errors.Add($"{path}.id: bắt buộc, 1–40 ký tự chữ/số/_/-");
            }
            else if (!ids.Add(id))
            {
                errors.Add($"{path}.id: trùng id '{id}' trong bài");
            }

            foreach (JsonProperty p in block.EnumerateObject())
            {
                if (p.Name is not ("id" or "type") && !allowed.Contains(p.Name))
                {
                    errors.Add($"{path}.{p.Name}: trường không được phép");
                }
            }

            switch (type)
            {
                case "heading":
                    if (!block.TryGetProperty("level", out JsonElement level) || level.ValueKind != JsonValueKind.Number
                        || !level.TryGetInt32(out int lv) || lv is not (2 or 3))
                    {
                        errors.Add($"{path}.level: chỉ 2 hoặc 3");
                    }

                    RequiredText(block, "text", 200, path, errors, text);
                    break;
                case "paragraph":
                    Spans(block, "spans", path, errors, text, required: true);
                    break;
                case "list":
                    if (block.TryGetProperty("ordered", out JsonElement ordered)
                        && ordered.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        errors.Add($"{path}.ordered: phải là true/false");
                    }

                    if (!block.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array
                        || items.GetArrayLength() is 0 or > MaxListItems)
                    {
                        errors.Add($"{path}.items: mảng 1–{MaxListItems} mục");
                        break;
                    }

                    int i = 0;
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        SpanArray(item, $"{path}.items[{i++}]", errors, text);
                    }

                    break;
                case "image":
                    if (!Guid.TryParse(Str(block, "mediaId"), out Guid mediaId))
                    {
                        errors.Add($"{path}.mediaId: phải là id ảnh (UUID)");
                    }
                    else
                    {
                        media.Add(mediaId);
                    }

                    RequiredText(block, "alt", 200, path, errors, text);
                    OptionalText(block, "caption", 300, path, errors, text);
                    break;
                case "callout":
                    if (Str(block, "tone") is not ("info" or "warn"))
                    {
                        errors.Add($"{path}.tone: info hoặc warn");
                    }

                    Spans(block, "spans", path, errors, text, required: true);
                    break;
                case "quote":
                    Spans(block, "spans", path, errors, text, required: true);
                    OptionalText(block, "cite", 200, path, errors, text);
                    break;
                case "cta":
                    RequiredText(block, "label", 60, path, errors, text);
                    if (Str(block, "href") is not { } href || !IsSafeHref(href))
                    {
                        errors.Add($"{path}.href: chỉ đường dẫn nội bộ /… hoặc https://");
                    }

                    break;
                case "faq":
                    RequiredText(block, "question", 300, path, errors, text);
                    Spans(block, "answer", path, errors, text, required: true);
                    break;
            }
        }
    }

    private static void Spans(JsonElement block, string name, string path, List<string> errors, StringBuilder text, bool required)
    {
        if (!block.TryGetProperty(name, out JsonElement spans))
        {
            if (required)
            {
                errors.Add($"{path}.{name}: bắt buộc");
            }

            return;
        }

        SpanArray(spans, $"{path}.{name}", errors, text);
    }

    private static void SpanArray(JsonElement spans, string path, List<string> errors, StringBuilder text)
    {
        if (spans.ValueKind != JsonValueKind.Array || spans.GetArrayLength() is 0 or > MaxSpans)
        {
            errors.Add($"{path}: mảng 1–{MaxSpans} đoạn chữ");
            return;
        }

        int i = 0;
        foreach (JsonElement span in spans.EnumerateArray())
        {
            string sp = $"{path}[{i++}]";
            if (span.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{sp}: đoạn chữ phải là object");
                continue;
            }

            foreach (JsonProperty p in span.EnumerateObject())
            {
                if (!SpanFields.Contains(p.Name))
                {
                    errors.Add($"{sp}.{p.Name}: trường không được phép");
                }
                else if (p.Name is "bold" or "italic" && p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add($"{sp}.{p.Name}: phải là true/false");
                }
            }

            RequiredText(span, "text", MaxSpanChars, sp, errors, text, allowWhitespace: true);
            if (span.TryGetProperty("href", out JsonElement href)
                && (href.ValueKind != JsonValueKind.String || !IsSafeHref(href.GetString()!)))
            {
                errors.Add($"{sp}.href: chỉ đường dẫn nội bộ /… hoặc https://");
            }
        }

        text.Append(' ');
    }

    private static void RequiredText(JsonElement obj, string name, int max, string path, List<string> errors, StringBuilder text,
        bool allowWhitespace = false)
    {
        string? value = Str(obj, name);
        if (value is null || value.Length > max || (!allowWhitespace && string.IsNullOrWhiteSpace(value)) || value.Length == 0)
        {
            errors.Add($"{path}.{name}: bắt buộc, 1–{max} ký tự");
            return;
        }

        text.Append(value).Append(allowWhitespace ? "" : " ");
    }

    private static void OptionalText(JsonElement obj, string name, int max, string path, List<string> errors, StringBuilder text)
    {
        if (!obj.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > max)
        {
            errors.Add($"{path}.{name}: chuỗi ≤ {max} ký tự hoặc null");
            return;
        }

        text.Append(v.GetString()).Append(' ');
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
