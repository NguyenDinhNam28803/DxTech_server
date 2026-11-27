using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Inventory.Contracts;
using server.Modules.Inventory.Crypto;
using server.Modules.Inventory.Domain;

namespace server.Modules.Inventory.Features;

internal sealed class KeyReservation(AppDbContext db, LicenseKeyProtector protector) : IKeyReservation
{
    public Task<int> ReserveAsync(Guid variantId, Guid orderItemId, int quantity, DateTimeOffset now, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            UPDATE inventory.license_keys
               SET status = 'reserved', order_item_id = {orderItemId}, reserved_at = {now}
             WHERE id IN (SELECT id FROM inventory.license_keys
                           WHERE variant_id = {variantId} AND status = 'available'
                           ORDER BY created_at
                           LIMIT {quantity}
                           FOR UPDATE SKIP LOCKED)
            """, ct);

    public Task<int> ReleaseAsync(IReadOnlyCollection<Guid> orderItemIds, CancellationToken ct)
    {
        Guid[] ids = orderItemIds.ToArray();
        return ids.Length == 0
            ? Task.FromResult(0)
            : db.Database.ExecuteSqlAsync($"""
                UPDATE inventory.license_keys
                   SET status = 'available', order_item_id = NULL, reserved_at = NULL
                 WHERE order_item_id = ANY({ids}) AND status = 'reserved'
                """, ct);
    }

    public Task<int> DeliverAsync(IReadOnlyCollection<Guid> orderItemIds, DateTimeOffset now, CancellationToken ct)
    {
        Guid[] ids = orderItemIds.ToArray();
        return ids.Length == 0
            ? Task.FromResult(0)
            : db.Database.ExecuteSqlAsync($"""
                UPDATE inventory.license_keys
                   SET status = 'delivered', delivered_at = {now}
                 WHERE order_item_id = ANY({ids}) AND status = 'reserved'
                """, ct);
    }

    public async Task<IReadOnlyList<DeliveredKey>> GetDeliveredKeysAsync(IReadOnlyCollection<Guid> orderItemIds, CancellationToken ct)
    {
        Guid[] ids = orderItemIds.ToArray();
        var rows = await db.Set<LicenseKey>().AsNoTracking()
            .Where(k => k.OrderItemId != null && ids.Contains(k.OrderItemId.Value) && k.Status == LicenseKeyStatus.Delivered)
            .OrderBy(k => k.DeliveredAt).ThenBy(k => k.Id)
            .Select(k => new { k.OrderItemId, k.VariantId, k.KeyVersion, k.KeyEncrypted, k.DeliveredAt })
            .ToListAsync(ct);

        return rows.Select(r => new DeliveredKey(r.OrderItemId!.Value, protector.Decrypt(r.VariantId, r.KeyVersion, r.KeyEncrypted), r.DeliveredAt!.Value))
            .ToList();
    }
}
