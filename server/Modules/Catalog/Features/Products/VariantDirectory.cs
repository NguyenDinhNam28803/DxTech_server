using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Catalog.Contracts;
using server.Modules.Catalog.Domain;

namespace server.Modules.Catalog.Features.Products;

internal sealed class VariantDirectory(AppDbContext db) : IVariantDirectory
{
    public async Task<VariantRef?> FindBySkuAsync(string sku, CancellationToken ct)
    {
        var v = await db.Set<ProductVariant>().AsNoTracking()
            .Where(x => x.Sku == sku)
            .Select(x => new { x.Id, x.Sku, x.Name, x.DeliveryType, x.IsActive })
            .SingleOrDefaultAsync(ct);
        return v is null ? null : new VariantRef(v.Id, v.Sku, v.Name, SnakeCaseEnumConverter<DeliveryType>.ToProvider(v.DeliveryType), v.IsActive);
    }
}
