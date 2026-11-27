using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence.Conventions;

namespace server.Infrastructure.Persistence;

/// <summary>
/// DbContext duy nhất của ứng dụng (ADR-002): mọi module dùng chung để thanh toán → cấp key → hoàn tất đơn
/// nằm trong một transaction. Mỗi module tự khai báo bảng của mình bằng <c>IEntityTypeConfiguration</c>
/// trong <c>Modules/&lt;Module&gt;/Persistence</c>, đặt vào schema riêng của module.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Extension dùng chung. Production: roles.sql (superuser) đã tạo sẵn; câu lệnh trong migration là
        // CREATE EXTENSION IF NOT EXISTS ⇒ không cần quyền gì thêm khi extension đã có (ADR-009).
        modelBuilder.HasPostgresExtension("unaccent");
        // citext: email, mã coupon so sánh không phân biệt hoa thường ngay ở UNIQUE (ADR-012, PR-7).
        modelBuilder.HasPostgresExtension("citext");

        // Nạp mọi cấu hình entity của các module trong assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Hàm SQL do migration tạo, dùng được trong LINQ (không sinh migration).
        server.Modules.Catalog.Persistence.CatalogDbFunctions.Register(modelBuilder);
        server.Modules.Content.Persistence.ContentDbFunctions.Register(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Add(_ => new SnakeCaseNamingConvention());
    }
}
