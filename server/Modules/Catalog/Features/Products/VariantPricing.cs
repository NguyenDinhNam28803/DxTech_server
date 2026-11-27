using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Contracts;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Features.Products;

internal sealed class VariantPricing(AppDbContext db) : IVariantPricing
{
    public async Task<IReadOnlyDictionary<Guid, PurchasableVariant>> GetPurchasableAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, PurchasableVariant>();
        }

        Guid[] ids = variantIds.Distinct().ToArray();
        var rows = await ProductListing.Listed(db)
            .SelectMany(p => p.Variants
                .Where(v => v.IsActive && ids.Contains(v.Id))
                .Select(v => new { v.Id, ProductSlug = p.Slug, ProductName = p.Name, v.Name, v.Sku, v.DeliveryType, v.PriceVnd, v.CompareAtPriceVnd }))
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id,
            r => new PurchasableVariant(r.Id, r.ProductSlug, r.ProductName, r.Name, r.Sku,
                SnakeCaseEnumConverter<DeliveryType>.ToProvider(r.DeliveryType), r.PriceVnd, r.CompareAtPriceVnd));
    }
}
