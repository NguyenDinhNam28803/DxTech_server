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

    public const string TestSigningKey = "integration-test-signing-key-0123456789abcdef";
    public const string TestLicenseKey = "dGVzdC1vbmx5LWxpY2Vuc2UtZW5jcnlwdGlvbi1rZXk=";
    public const string TestHmacKey = "dGVzdC1vbmx5LWxpY2Vuc2UtaG1hYy1rZXktMDAwMDA=";

    /// <summary>Ghi đè chuỗi kết nối; <c>null</c> = dùng <see cref="ConnectionString"/>.</summary>
    protected virtual string? ConnectionStringOverride => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing": không Swagger, không HTTPS redirect, không đọc appsettings.Development.json.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionStringOverride ?? ConnectionString);
        // ADR-012: khoá ký chỉ dùng trong test; nới giới hạn tần suất để các test không chặn nhau
        // (test riêng cho 429 dùng factory với giới hạn nhỏ). Kiểm tra admin trong DB không cache.
        builder.UseSetting("Auth:SigningKeys:0", TestSigningKey);
        builder.UseSetting("Auth:AdminCheckCacheSeconds", "0");
        builder.UseSetting("Auth:RateLimits:LoginPerMinute", "10000");
        builder.UseSetting("Auth:RateLimits:LoginPerHour", "10000");
        builder.UseSetting("Auth:RateLimits:RegisterPerHour", "10000");
        builder.UseSetting("Auth:RateLimits:RefreshPerMinute", "10000");
        builder.UseSetting("Auth:RateLimits:OtherPerMinute", "10000");
        builder.UseSetting("Auth:RateLimits:CartQuotePerMinute", "10000");
        builder.UseSetting("Auth:RateLimits:OrdersPerHour", "10000");
        // ADR-014: khoá license chỉ dùng trong test. ADR-013: tài khoản nhận tiền giả để mở đặt hàng.
        builder.UseSetting("License:CurrentVersion", "1");
        builder.UseSetting("License:EncryptionKeys:1", TestLicenseKey);
        builder.UseSetting("License:HmacKey", TestHmacKey);
        builder.UseSetting("Payments:BankTransfer:BankBin", "970436");
        builder.UseSetting("Payments:BankTransfer:BankName", "Vietcombank");
        builder.UseSetting("Payments:BankTransfer:AccountNumber", "0011001234567");
        builder.UseSetting("Payments:BankTransfer:AccountName", "CONG TY DXTECHS");

        builder.ConfigureTestServices(services =>
        {
            // Nạp controller chỉ dùng cho test (TestErrorsController) từ assembly test.
            services.AddControllers().AddApplicationPart(typeof(ApiFactory).Assembly);
        });
    }
}
