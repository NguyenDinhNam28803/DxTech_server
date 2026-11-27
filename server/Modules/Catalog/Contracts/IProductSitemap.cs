namespace server.Modules.Catalog.Contracts;

/// <summary>Sản phẩm đang bán cho sitemap (module Content, CT-2). Web: <c>/san-pham/{slug}</c>.</summary>
public interface IProductSitemap
{
    Task<IReadOnlyList<ProductSitemapEntry>> ListAsync(CancellationToken ct);
}

public sealed record ProductSitemapEntry(string Slug, DateTimeOffset UpdatedAt);
