using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Interceptors;

namespace server.IntegrationTests.Infrastructure;

/// <summary>
/// Database test dùng chung: áp migration MỘT lần cho cả lần chạy test (các class test chạy song song
/// nên phải dùng chung một Task), rồi cấp DbContext mới cho từng thao tác.
/// </summary>
internal static class TestDatabase
{
    private static readonly Lazy<Task> Migration = new(async () =>
    {
        await using AppDbContext db = CreateContext();
        await db.Database.MigrateAsync();
    });

    public static Task EnsureMigratedAsync() => Migration.Value;

    public static AppDbContext CreateContext(TimeProvider? timeProvider = null)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ApiFactory.ConnectionString)
            .AddInterceptors(new AuditableInterceptor(timeProvider ?? TimeProvider.System))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Hậu tố ngẫu nhiên cho slug/sku để các test không đụng nhau và chạy lại được nhiều lần.</summary>
    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..10]}";

    /// <summary>
    /// Một "từ" ngẫu nhiên chỉ gồm chữ a–z, dùng làm dấu hiệu trong test tìm kiếm full-text. Không dùng hex:
    /// bộ tách từ của PostgreSQL đọc chuỗi kiểu "1e57…" thành số ⇒ test lúc qua lúc trượt.
    /// </summary>
    public static string Word() => "zq" + new string(Guid.NewGuid().ToByteArray().Select(b => (char)('a' + (b % 26))).ToArray());
}
