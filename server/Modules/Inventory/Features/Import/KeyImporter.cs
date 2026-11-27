using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Modules.Catalog.Contracts;
using server.Modules.Inventory.Crypto;
using server.Modules.Inventory.Domain;

namespace server.Modules.Inventory.Features.Import;

/// <param name="Invalid">Số thứ tự dòng (bắt đầu từ 1) bị bỏ vì sai định dạng — không in nội dung dòng (là key).</param>
internal sealed record ImportReport(string Sku, int Total, int Inserted, int Duplicates, IReadOnlyList<int> Invalid, bool DryRun, Guid? BatchId);

/// <summary>
/// Nhập key cho một gói (lệnh <c>--import-keys</c>, PR-11; màn hình admin ở Phase 4).
/// Mỗi dòng một key; bỏ dòng trống và dòng bắt đầu bằng <c>#</c>. Trùng (trong file hoặc đã có trong kho, so bằng HMAC
/// của key chuẩn hoá) ⇒ bỏ qua và đếm. Một transaction: lỗi giữa chừng ⇒ không ghi gì. Không bao giờ in/ghi log key.
/// </summary>
internal sealed class KeyImporter(AppDbContext db, IVariantDirectory variants, LicenseKeyProtector protector, TimeProvider time)
{
    public const int MinLength = 5;
    public const int MaxLength = 200;
    public const int MaxRows = 10_000;

    public async Task<ImportReport> RunAsync(string sku, IEnumerable<string> lines, string fileName, string? note, bool dryRun, CancellationToken ct)
    {
        VariantRef variant = await variants.FindBySkuAsync(sku, ct)
            ?? throw new InvalidOperationException($"Không có gói SKU '{sku}' (hoặc đã xoá).");
        if (variant.DeliveryType != DeliveryTypes.LicenseKey)
        {
            throw new InvalidOperationException($"Gói '{sku}' giao bằng '{variant.DeliveryType}', không dùng license key.");
        }

        // Đọc + phân loại trong bộ nhớ (tối đa 10.000 dòng).
        var candidates = new List<(string Key, byte[] Hash)>();
        var invalid = new List<int>();
        var seen = new HashSet<string>();
        int total = 0, duplicates = 0, lineNo = 0;
        foreach (string raw in lines)
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (++total > MaxRows)
            {
                throw new InvalidOperationException($"Tối đa {MaxRows} key mỗi lần nhập — chia nhỏ file.");
            }

            string normalized = LicenseKeyProtector.Normalize(line);
            if (normalized.Length is < MinLength or > MaxLength || line.Any(char.IsControl))
            {
                invalid.Add(lineNo);
                continue;
            }

            byte[] hash = protector.Hash(line);
            if (!seen.Add(Convert.ToHexString(hash)))
            {
                duplicates++;
                continue;
            }

            candidates.Add((line, hash));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        byte[][] hashes = candidates.Select(c => c.Hash).ToArray();
        HashSet<string> existing = (await db.Set<LicenseKey>().AsNoTracking()
                .Where(k => hashes.Contains(k.KeyHash)).Select(k => k.KeyHash).ToListAsync(ct))
            .Select(Convert.ToHexString).ToHashSet();

        DateTimeOffset now = time.GetUtcNow();
        var batch = new ImportBatch(variant.Id, ImportSource.Cli, fileName, now, note: note);
        db.Add(batch);
        int inserted = 0;
        foreach ((string key, byte[] hash) in candidates)
        {
            if (existing.Contains(Convert.ToHexString(hash)))
            {
                duplicates++;
                continue;
            }

            db.Add(new LicenseKey(variant.Id, batch.Id, protector.Encrypt(variant.Id, key), protector.CurrentVersion, hash,
                LicenseKeyProtector.Hint(key), now));
            inserted++;
        }

        batch.Record(inserted, duplicates, invalid.Count);
        await db.SaveChangesAsync(ct);
        if (dryRun)
        {
            await tx.RollbackAsync(ct);
            return new ImportReport(sku, total, inserted, duplicates, invalid, true, null);
        }

        await tx.CommitAsync(ct);
        return new ImportReport(sku, total, inserted, duplicates, invalid, false, batch.Id);
    }
}
