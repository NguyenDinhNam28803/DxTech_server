using server.Modules.Content.Domain;

namespace server.Modules.Content.Features;

/// <summary>
/// Đường dẫn trên web (client Vue) của chủ đề/bài — một nơi duy nhất, dùng cho API (trường <c>path</c>), sitemap và AI tư vấn
/// (dẫn nguồn). Khớp router hiện tại: <c>/tin-tuc/:slug</c>, <c>/giai-phap/:nhóm/:trang?/:hạng-mục?</c>, <c>/tai-nguyen/:mục?/:chủ-đề?</c>.
/// null = loại nội dung không có trang riêng (sách, tài liệu, thuật ngữ hiển thị trong trang tài nguyên).
/// </summary>
internal static class WebPaths
{
    /// <param name="topicChain">Slug chủ đề từ gốc tới chủ đề của bài/chủ đề.</param>
    public static string? Topic(PostType type, IReadOnlyList<string> topicChain) => type switch
    {
        PostType.Solution or PostType.Training when topicChain.Count > 0 => "/giai-phap/" + string.Join('/', topicChain),
        PostType.Resource when topicChain.Count > 0 => "/tai-nguyen/" + string.Join('/', topicChain),
        PostType.News => "/tin-tuc",
        _ => null,
    };

    public static string? Post(PostType type, string slug, IReadOnlyList<string> topicChain, string? parentSlug) => type switch
    {
        PostType.News => $"/tin-tuc/{slug}",
        PostType.Solution or PostType.Training when topicChain.Count > 0
            => $"/giai-phap/{topicChain[0]}/" + (parentSlug is null ? slug : $"{parentSlug}/{slug}"),
        PostType.Page => $"/{slug}",
        _ => null,
    };
}
