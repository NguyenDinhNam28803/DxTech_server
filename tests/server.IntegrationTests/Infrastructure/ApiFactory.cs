using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace server.IntegrationTests.Infrastructure;

/// <summary>
/// Dựng API thật trong bộ nhớ, nối với PostgreSQL thật (ADR-005).
/// Chuỗi kết nối lấy từ biến môi trường <c>TEST_CONNECTION_STRING</c>
/// (CI đặt biến này trỏ tới service container), mặc định trỏ tới PostgreSQL của
/// <c>docker compose up -d</c> trên máy dev.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=appdb;Username=appuser;Password=apppassword";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_CONNECTION_STRING") is { Length: > 0 } fromEnv
            ? fromEnv
            : DefaultConnectionString;

    /// <summary>Ghi đè chuỗi kết nối; <c>null</c> = dùng <see cref="ConnectionString"/>.</summary>
    protected virtual string? ConnectionStringOverride => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing": không Swagger, không HTTPS redirect, không đọc appsettings.Development.json.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionStringOverride ?? ConnectionString);

        builder.ConfigureTestServices(services =>
        {
            // Nạp controller chỉ dùng cho test (TestErrorsController) từ assembly test.
            services.AddControllers().AddApplicationPart(typeof(ApiFactory).Assembly);
        });
    }
}
