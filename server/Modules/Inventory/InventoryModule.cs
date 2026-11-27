using Microsoft.Extensions.Options;
using server.Modules.Inventory.Crypto;
using server.Modules.Inventory.Features;
using server.Modules.Inventory.Features.Import;

namespace server.Modules.Inventory;

/// <summary>
/// Module Inventory: kho license key (mã hoá AES-256-GCM — ADR-014) và lô nhập — schema PostgreSQL <c>inventory</c>.
/// PR-9: bảng. PR-10: đếm tồn. PR-11: mã hoá, giữ/trả key, lệnh <c>--import-keys</c>.
/// </summary>
public static class InventoryModule
{
    public const string Schema = "inventory";

    public static IServiceCollection AddInventoryModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LicenseOptions>()
            .Bind(configuration.GetSection(LicenseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => new LicenseKeyProtector(sp.GetRequiredService<IOptions<LicenseOptions>>()));

        services.AddScoped<Contracts.IStockQuery, StockQuery>();
        services.AddScoped<Contracts.IKeyReservation>(sp => new KeyReservation(
            sp.GetRequiredService<Infrastructure.Persistence.AppDbContext>(), sp.GetRequiredService<LicenseKeyProtector>()));
        services.AddScoped(sp => new KeyImporter(
            sp.GetRequiredService<Infrastructure.Persistence.AppDbContext>(),
            sp.GetRequiredService<Catalog.Contracts.IVariantDirectory>(),
            sp.GetRequiredService<LicenseKeyProtector>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Lệnh <c>--import-keys &lt;SKU&gt; [--note "Hoá đơn …"] [--dry-run]</c>: đọc key từ stdin (mỗi dòng một key) rồi thoát.
    /// Đọc stdin để file key không phải nằm trong image/container: trên VPS chạy
    /// <c>$C run --rm --no-deps -T api --import-keys SKU &lt; keys.txt</c>, xong thì xoá file (<c>shred -u keys.txt</c>).
    /// Chỉ in số lượng và số thứ tự dòng lỗi — không in key. Mã thoát: 0 thành công, 1 lỗi, 2 sai cú pháp.
    /// </summary>
    public static async Task<int> ImportKeysAsync(IServiceProvider services, string[] args, TextReader input, TextWriter output, CancellationToken ct = default)
    {
        int i = Array.IndexOf(args, "--import-keys");
        string? sku = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        int n = Array.IndexOf(args, "--note");
        string? note = n >= 0 && n + 1 < args.Length ? args[n + 1] : null;
        bool dryRun = args.Contains("--dry-run");
        if (sku is null)
        {
            await output.WriteLineAsync("Dùng: --import-keys <SKU> [--note \"Số hoá đơn\"] [--dry-run]   (key đọc từ stdin, mỗi dòng một key)");
            return 2;
        }

        var lines = new List<string>();
        while (await input.ReadLineAsync(ct) is { } line)
        {
            lines.Add(line);
        }

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        try
        {
            ImportReport r = await scope.ServiceProvider.GetRequiredService<KeyImporter>()
                .RunAsync(sku, lines, "stdin", note, dryRun, ct);
            await output.WriteLineAsync(
                $"import-keys{(r.DryRun ? " (DRY RUN — đã rollback, không ghi gì)" : "")}: {r.Sku} — " +
                $"tổng {r.Total}, nhập {r.Inserted}, trùng {r.Duplicates}, sai định dạng {r.Invalid.Count}" +
                (r.BatchId is { } b ? $" · lô {b}" : ""));
            if (r.Invalid.Count > 0)
            {
                await output.WriteLineAsync($"  Dòng sai định dạng (5–200 ký tự, không ký tự điều khiển): {string.Join(", ", r.Invalid.Take(50))}");
            }

            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            await output.WriteLineAsync($"import-keys THẤT BẠI — không ghi gì: {ex.GetBaseException().Message}");
            return 1;
        }
    }
}
