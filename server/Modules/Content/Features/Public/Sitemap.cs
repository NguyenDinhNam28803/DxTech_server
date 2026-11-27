using System.Globalization;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using server.Modules.Catalog.Contracts;
using server.Modules.Content.Domain;

namespace server.Modules.Content.Features.Public;

/// <summary>
/// <c>sitemap.xml</c> (ADR-015 §7): trang tĩnh + mọi bài đã đăng có trang riêng + chủ đề có trang + sản phẩm đang bán.
/// Chỉ dùng <see cref="PublicContentHandler.Visible"/> ⇒ bản nháp/hẹn giờ không lọt ra. Dưới 50.000 URL (giới hạn của giao thức) ở quy mô hiện tại.
/// </summary>
public sealed class SitemapHandler
{
    private static readonly string[] StaticPaths =
        ["/", "/gioi-thieu", "/lien-he", "/dich-vu", "/phan-mem", "/du-an", "/tin-tuc", "/tai-nguyen", "/chinh-sach"];

    private readonly PublicContentHandler _content;
    private readonly IProductSitemap _products;
    private readonly ContentOptions _options;

    internal SitemapHandler(PublicContentHandler content, IProductSitemap products, IOptions<ContentOptions> options)
    {
        _content = content;
        _products = products;
        _options = options.Value;
    }

    public async Task<string> BuildAsync(CancellationToken ct)
    {
        var urls = new List<(string Path, DateTimeOffset? LastModified)>();
        urls.AddRange(StaticPaths.Select(p => (p, (DateTimeOffset?)null)));

        foreach (PostType type in Enum.GetValues<PostType>())
        {
            TopicIndex topics = await _content.TopicsAsync(type, ct);
            foreach (Topic t in topics.ChildrenOf(null).SelectMany(Flatten))
            {
                if (WebPaths.Topic(type, topics.Chain(t.Id)) is { } topicPath)
                {
                    urls.Add((topicPath, t.UpdatedAt));
                }
            }

            var posts = await _content.Visible().Where(p => p.PostType == type)
                .Select(p => new
                {
                    p.Slug, p.TopicId, p.UpdatedAt,
                    ParentSlug = _content.Visible().Where(x => x.Id == p.ParentId).Select(x => x.Slug).FirstOrDefault(),
                    HasParent = p.ParentId != null,
                })
                .ToListAsync(ct);
            foreach (var p in posts.Where(p => !p.HasParent || p.ParentSlug is not null)) // cha bị ẩn ⇒ con cũng ẩn
            {
                IReadOnlyList<string> chain = p.TopicId is { } tid ? topics.Chain(tid) : [];
                if (WebPaths.Post(type, p.Slug, chain, p.ParentSlug) is { } path)
                {
                    urls.Add((path, p.UpdatedAt));
                }
            }

            IEnumerable<Topic> Flatten(Topic t) => new[] { t }.Concat(topics.ChildrenOf(t.Id).SelectMany(Flatten));
        }

        urls.AddRange((await _products.ListAsync(ct)).Select(p => ($"/san-pham/{p.Slug}", (DateTimeOffset?)p.UpdatedAt)));

        string site = _options.SiteUrl.TrimEnd('/');
        var sb = new System.Text.StringBuilder();
        using (var xml = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = System.Text.Encoding.UTF8 }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
            foreach ((string path, DateTimeOffset? lastMod) in urls.DistinctBy(u => u.Path))
            {
                xml.WriteStartElement("url");
                xml.WriteElementString("loc", site + path);
                if (lastMod is { } m)
                {
                    xml.WriteElementString("lastmod", m.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        // StringBuilder luôn UTF-16 ⇒ sửa khai báo cho đúng nội dung trả về (UTF-8).
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }
}
