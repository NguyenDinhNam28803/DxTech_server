using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Inventory.Contracts;

namespace server.Modules.Inventory.Features;

internal sealed class StockQuery(AppDbContext db) : IStockQuery
{
    public async Task<IReadOnlyDictionary<Guid, int>> CountAvailableAsync(IReadOnlyCollection<Guid> variantIds, int cap, CancellationToken ct)
    {
        if (variantIds.Count == 0 || cap <= 0)
        {
            return new Dictionary<Guid, int>();
        }

        Guid[] ids = variantIds.Distinct().ToArray();
        // LATERAL + LIMIT: mỗi gói chỉ đọc tối đa `cap` dòng của index một phần — chi phí không tăng theo số key trong kho.
        List<StockRow> rows = await db.Database.SqlQuery<StockRow>($"""
            SELECT v.id AS "VariantId", s.n AS "Available"
              FROM unnest({ids}) AS v(id)
              CROSS JOIN LATERAL (
                SELECT count(*)::int AS n FROM (
                  SELECT 1 FROM inventory.license_keys k
                   WHERE k.variant_id = v.id AND k.status = 'available'
                   LIMIT {cap}) x
              ) s
             WHERE s.n > 0
            """).ToListAsync(ct);

        return rows.ToDictionary(r => r.VariantId, r => r.Available);
    }

    internal sealed class StockRow
    {
        public Guid VariantId { get; set; }

        public int Available { get; set; }
    }
}
