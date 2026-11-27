using Microsoft.Extensions.Options;
using server.Infrastructure.Persistence;
using server.Modules.Content.Features;
using server.Modules.Content.Features.Admin;
using server.Modules.Content.Features.Public;
using server.Modules.Content.Jobs;
using server.Modules.Content.Seeding;

namespace server.Modules.Content;

/// <summary>
/// Module Content (ADR-015): tin tức, giải pháp, đào tạo, tài nguyên, thuật ngữ, trang tĩnh; ảnh; lịch khai giảng; lead —
/// schema PostgreSQL <c>content</c>. Bố cục giống Catalog. CT-1: bảng + <c>--seed-content</c>; CT-2: API công khai;
/// CT-3: API quản trị + media. Thiết kế: <c>docs/design/ct-content-module.md</c>.
/// </summary>
public static class ContentModule
{
    public const string Schema = "content";

    public static IServiceCollection AddContentModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ContentOptions>()
            .Bind(configuration.GetSection(ContentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Handler có constructor internal ⇒ đăng ký tường minh (như các module khác).
        services.AddScoped(sp => new PublicContentHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<ContentOptions>>()));
        services.AddScoped(sp => new SubmitLeadHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<ContentOptions>>()));
        services.AddScoped(sp => new SitemapHandler(
            sp.GetRequiredService<PublicContentHandler>(), sp.GetRequiredService<Catalog.Contracts.IProductSitemap>(),
            sp.GetRequiredService<IOptions<ContentOptions>>()));
        services.AddScoped(sp => new AdminPostsHandler(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new AdminContentHandler(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new AdminMediaHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<ContentOptions>>()));
        services.AddScoped<ContentSeeder>();

        services.AddSingleton<ContentMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<ContentMaintenance>());
        return services;
    }

    /// <summary>Lệnh <c>--seed-content [--dry-run]</c>. Mã thoát 0 thành công, 1 lỗi (không ghi gì).</summary>
    public static async Task<int> SeedContentAsync(IServiceProvider services, bool dryRun, TextWriter output, CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        try
        {
            ContentSeedReport r = await scope.ServiceProvider.GetRequiredService<ContentSeeder>().RunAsync(dryRun, ct);
            await output.WriteLineAsync(
                $"seed-content{(r.DryRun ? " (DRY RUN — đã rollback, không ghi gì)" : "")} từ {r.Source}: " +
                $"chủ đề +{r.TopicsCreated} (bỏ qua {r.TopicsSkipped}), bài +{r.PostsCreated} (bỏ qua {r.PostsSkipped}), tác giả {r.AuthorEmail}");
            return 0;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"seed-content THẤT BẠI — không ghi gì (transaction rollback): {ex.GetBaseException().Message}");
            return 1;
        }
    }
}
