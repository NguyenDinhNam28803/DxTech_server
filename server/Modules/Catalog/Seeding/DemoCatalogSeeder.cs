using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Seeding;

/// <summary>Kết quả một lần seed: con số in ra cho người vận hành.</summary>
internal sealed record SeedReport(
    int CategoriesCreated, int CategoriesSkipped,
    int ProductsCreated, int ProductsSkipped,
    int VariantsCreated, bool DryRun);

/// <summary>
/// Nạp dữ liệu DEMO của catalog (ADR-010) từ <c>demo-catalog.json</c> (nhúng trong assembly, chuyển từ
/// <c>src/data/products.ts</c> của client). Chỉ chạy khi gọi tường minh <c>--seed-demo</c>, không tự chạy lúc khởi động.
///
/// Idempotent theo kiểu "chỉ thêm cái còn thiếu": danh mục/sản phẩm đã có slug (kể cả bản đã xoá mềm) thì BỎ QUA,
/// không ghi đè. Lý do: sau khi seed, admin có thể sửa giá/mô tả hoặc xoá mềm sản phẩm demo — chạy lại seed không
/// được âm thầm hoàn tác các thay đổi đó hay "hồi sinh" sản phẩm đã xoá.
///
/// Toàn bộ chạy trong MỘT transaction: lỗi ở bất kỳ dòng nào ⇒ không ghi gì. <c>dryRun</c> chạy đủ (kể cả ràng buộc
/// của database) rồi rollback, để xem trước con số.
/// </summary>
internal sealed class DemoCatalogSeeder(AppDbContext db)
{
    public const string ResourceName = "server.Modules.Catalog.Seeding.demo-catalog.json";

    public async Task<SeedReport> RunAsync(bool dryRun, CancellationToken ct)
    {
        DemoCatalog data = Load();

        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);

        // Danh mục: tra theo slug trong TẤT CẢ bản ghi (kể cả đã xoá mềm) để không tạo bản trùng slug với bản đã xoá.
        Dictionary<string, Category> categories = await db.Set<Category>().IgnoreQueryFilters()
            .ToDictionaryAsync(c => c.Slug, ct);

        int categoriesCreated = 0, categoriesSkipped = 0;
        foreach (SeedCategory seed in data.Categories)
        {
            if (categories.ContainsKey(seed.Slug))
            {
                categoriesSkipped++;
                continue;
            }

            Category parent = categories.GetValueOrDefault(seed.ParentSlug)
                ?? throw new InvalidOperationException($"Danh mục cha '{seed.ParentSlug}' không tồn tại (cần migration Catalog_Initial).");
            var category = new Category(seed.Slug, seed.Name, parent.Id) { SortOrder = seed.SortOrder };
            db.Add(category);
            categories[seed.Slug] = category;
            categoriesCreated++;
        }

        HashSet<string> existingProducts = (await db.Set<Product>().IgnoreQueryFilters()
            .Select(p => p.Slug).ToListAsync(ct)).ToHashSet();

        int productsCreated = 0, productsSkipped = 0, variantsCreated = 0;
        foreach (SeedProduct seed in data.Products)
        {
            if (existingProducts.Contains(seed.Slug))
            {
                productsSkipped++;
                continue;
            }

            Category category = categories.GetValueOrDefault(seed.CategorySlug)
                ?? throw new InvalidOperationException($"Sản phẩm '{seed.Slug}': không có danh mục '{seed.CategorySlug}'.");

            var product = new Product(category.Id, seed.Slug, seed.Name, SnakeCaseEnumConverter<LicenseType>.FromProvider(seed.LicenseType))
            {
                Brand = seed.Brand,
                ShortDescription = seed.ShortDescription,
                Platforms = [.. seed.Platforms],
                Features = [.. seed.Features],
                ImageUrl = seed.ImageUrl,
                Badge = seed.Badge,
            };

            foreach ((SeedVariant v, int index) in seed.Variants.Select((v, i) => (v, i)))
            {
                product.Variants.Add(new ProductVariant(
                    product.Id, v.Sku, v.Name, v.PriceVnd, SnakeCaseEnumConverter<DeliveryType>.FromProvider(v.DeliveryType))
                {
                    TermMonths = v.TermMonths,
                    Seats = v.Seats,
                    CompareAtPriceVnd = v.CompareAtPriceVnd,
                    IsDefault = v.IsDefault,
                    SortOrder = (index + 1) * 10,
                });
                variantsCreated++;
            }

            db.Add(product);
            productsCreated++;
        }

        // Một lần SaveChanges: ràng buộc CHECK/UNIQUE của database kiểm tra toàn bộ dữ liệu seed.
        await db.SaveChangesAsync(ct);

        if (dryRun)
        {
            await tx.RollbackAsync(ct);
        }
        else
        {
            await tx.CommitAsync(ct);
        }

        return new SeedReport(categoriesCreated, categoriesSkipped, productsCreated, productsSkipped, variantsCreated, dryRun);
    }

    internal static DemoCatalog Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Không tìm thấy dữ liệu seed nhúng '{ResourceName}'.");
        return JsonSerializer.Deserialize<DemoCatalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("demo-catalog.json rỗng.");
    }

    internal sealed record DemoCatalog(string Source, IReadOnlyList<SeedCategory> Categories, IReadOnlyList<SeedProduct> Products);

    internal sealed record SeedCategory(string Slug, string Name, string ParentSlug, int SortOrder);

    internal sealed record SeedProduct(
        string Slug, string Name, string CategorySlug, string? Brand, string? ShortDescription, string LicenseType,
        IReadOnlyList<string> Platforms, IReadOnlyList<string> Features, string? ImageUrl, string? Badge,
        IReadOnlyList<SeedVariant> Variants);

    internal sealed record SeedVariant(
        string Sku, string Name, int? TermMonths, int Seats, long PriceVnd, long? CompareAtPriceVnd,
        string DeliveryType, bool IsDefault);
}
