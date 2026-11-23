using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;
using server.Modules.Catalog.Persistence;
using server.Shared.Paging;
using server.Shared.Results;

namespace server.Modules.Catalog.Features.Products;

/// <summary>Tham số của <c>GET /api/products</c> và <c>GET /api/products/search</c> (bind từ query string).</summary>
public sealed class ListProductsQuery
{
    public const int DefaultPageSize = 12;

    /// <summary>Slug danh mục; gồm cả danh mục con cháu.</summary>
    public string? Category { get; init; }

    /// <summary>windows | macos | linux | ios | android | web.</summary>
    public string? Platform { get; init; }

    /// <summary>perpetual | subscription | service.</summary>
    public string? LicenseType { get; init; }

    /// <summary>Khoảng giá (VND): sản phẩm có ít nhất một gói đang bán nằm trong khoảng.</summary>
    public long? MinPrice { get; init; }

    public long? MaxPrice { get; init; }

    /// <summary>Từ khoá, không phân biệt dấu/hoa thường, khớp tiền tố.</summary>
    public string? Q { get; init; }

    /// <summary>relevance (chỉ khi có q) | price_asc | price_desc | newest. Mặc định: relevance nếu có q, ngược lại newest.</summary>
    public string? Sort { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;
}

public sealed record CategoryRef(string Slug, string Name);

/// <summary>Một thẻ sản phẩm trong danh sách. Giá lấy từ gói rẻ nhất đang bán ("Từ 690.000đ", gạch giá gốc của chính gói đó).</summary>
public sealed record ProductListItem(
    Guid Id,
    string Slug,
    string Name,
    string? ShortDescription,
    string? Brand,
    string LicenseType,
    IReadOnlyList<string> Platforms,
    string? ImageUrl,
    string? Badge,
    CategoryRef Category,
    long MinPriceVnd,
    long? CompareAtPriceVnd,
    int VariantCount);

public sealed class ListProductsHandler(AppDbContext db)
{
    private static readonly string[] Sorts = ["relevance", "price_asc", "price_desc", "newest"];

    public async Task<Result<PagedResult<ProductListItem>>> HandleAsync(ListProductsQuery query, CancellationToken ct)
    {
        string? tsQuery = SearchText.ToTsQuery(query.Q);
        string sort = query.Sort ?? (tsQuery is null ? "newest" : "relevance");

        Dictionary<string, string[]> errors = Validate(query, sort, tsQuery);
        if (errors.Count > 0)
        {
            return Error.Validation("catalog.invalid_query", "Tham số lọc sản phẩm không hợp lệ.", errors);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        IQueryable<Product> products = ProductListing.Listed(db);

        if (query.Category is { Length: > 0 } categorySlug)
        {
            List<Guid> categoryIds = await ProductListing.CategoryWithDescendantsAsync(db, categorySlug, ct);
            if (categoryIds.Count == 0)
            {
                return Error.NotFound("catalog.category_not_found", $"Không tìm thấy danh mục '{categorySlug}'.");
            }

            products = products.Where(p => categoryIds.Contains(p.CategoryId));
        }

        if (query.Platform is { Length: > 0 } platform)
        {
            products = products.Where(p => p.Platforms.Contains(platform));
        }

        if (query.LicenseType is { Length: > 0 } licenseText)
        {
            LicenseType license = SnakeCaseEnumConverter<LicenseType>.FromProvider(licenseText);
            products = products.Where(p => p.LicenseType == license);
        }

        if (query.MinPrice is not null || query.MaxPrice is not null)
        {
            long min = query.MinPrice ?? 0;
            long max = query.MaxPrice ?? long.MaxValue;
            products = products.Where(p => p.Variants.Any(v => v.IsActive && v.PriceVnd >= min && v.PriceVnd <= max));
        }

        if (tsQuery is not null)
        {
            products = products.Where(p => EF.Property<NpgsqlTsVector>(p, ProductConfiguration.SearchVector)
                .Matches(EF.Functions.ToTsQuery("simple", CatalogDbFunctions.Unaccent(tsQuery))));
        }

        long total = await products.LongCountAsync(ct);
        if (total == 0)
        {
            return PagedResult<ProductListItem>.Empty(page);
        }

        // Thứ tự luôn kết thúc bằng Id để phân trang ổn định (không lặp/sót sản phẩm giữa các trang).
        IOrderedQueryable<Product> ordered = sort switch
        {
            "price_asc" => products.OrderBy(p => p.Variants.Where(v => v.IsActive).Min(v => v.PriceVnd)).ThenBy(p => p.Id),
            "price_desc" => products.OrderByDescending(p => p.Variants.Where(v => v.IsActive).Min(v => v.PriceVnd)).ThenBy(p => p.Id),
            "relevance" => products
                .OrderByDescending(p => EF.Property<NpgsqlTsVector>(p, ProductConfiguration.SearchVector)
                    .Rank(EF.Functions.ToTsQuery("simple", CatalogDbFunctions.Unaccent(tsQuery!))))
                .ThenBy(p => p.Id),
            _ => products.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id),
        };

        var rows = await ordered
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(p => new
            {
                p.Id, p.Slug, p.Name, p.ShortDescription, p.Brand, p.LicenseType, p.Platforms, p.ImageUrl, p.Badge,
                CategorySlug = p.Category!.Slug,
                CategoryName = p.Category.Name,
                // Hai subquery vô hướng (tương quan) ⇒ PostgreSQL chỉ tính cho các dòng của trang hiện tại.
                // Không chiếu cả object "gói rẻ nhất": EF dịch thành ROW_NUMBER() trên TOÀN BỘ bảng gói.
                MinPriceVnd = p.Variants.Where(v => v.IsActive).Min(v => v.PriceVnd),
                CompareAtPriceVnd = p.Variants.Where(v => v.IsActive)
                    .OrderBy(v => v.PriceVnd).ThenBy(v => v.SortOrder)
                    .Select(v => v.CompareAtPriceVnd)
                    .FirstOrDefault(),
                VariantCount = p.Variants.Count(v => v.IsActive),
            })
            .ToListAsync(ct);

        List<ProductListItem> items = rows.Select(r => new ProductListItem(
                r.Id, r.Slug, r.Name, r.ShortDescription, r.Brand,
                SnakeCaseEnumConverter<LicenseType>.ToProvider(r.LicenseType),
                r.Platforms, r.ImageUrl, r.Badge,
                new CategoryRef(r.CategorySlug, r.CategoryName),
                r.MinPriceVnd, r.CompareAtPriceVnd, r.VariantCount))
            .ToList();

        return new PagedResult<ProductListItem>(items, page.Page, page.PageSize, total);
    }

    private static Dictionary<string, string[]> Validate(ListProductsQuery query, string sort, string? tsQuery)
    {
        var errors = new Dictionary<string, string[]>();

        if (query.Platform is { Length: > 0 } platform && !Platforms.All.Contains(platform))
        {
            errors["platform"] = [$"Nền tảng phải là một trong: {string.Join(", ", Platforms.All)}."];
        }

        if (query.LicenseType is { Length: > 0 } license
            && !SnakeCaseEnumConverter<LicenseType>.DbValues.Contains(license))
        {
            errors["licenseType"] = [$"Loại giấy phép phải là một trong: {string.Join(", ", SnakeCaseEnumConverter<LicenseType>.DbValues)}."];
        }

        if (query.MinPrice < 0)
        {
            errors["minPrice"] = ["Giá thấp nhất không được âm."];
        }

        if (query.MaxPrice < 0)
        {
            errors["maxPrice"] = ["Giá cao nhất không được âm."];
        }
        else if (query.MinPrice is { } min && query.MaxPrice is { } max && min > max)
        {
            errors["maxPrice"] = ["Giá cao nhất phải lớn hơn hoặc bằng giá thấp nhất."];
        }

        if (!Sorts.Contains(sort))
        {
            errors["sort"] = [$"Cách sắp xếp phải là một trong: {string.Join(", ", Sorts)}."];
        }
        else if (sort == "relevance" && tsQuery is null)
        {
            errors["sort"] = ["Sắp xếp theo độ liên quan cần có từ khoá tìm kiếm (q)."];
        }

        return errors;
    }
}
