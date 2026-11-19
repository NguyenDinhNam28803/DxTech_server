using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Interceptors;

namespace server.IntegrationTests.Infrastructure;

/// <summary>Health check, kết nối PostgreSQL thật và cấu hình DI của hạ tầng.</summary>
public class HealthAndDatabaseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Liveness_is_healthy()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_checks_the_real_database()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Db_context_connects_and_server_works_in_utc()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await db.Database.CanConnectAsync());

        // Npgsql 8 luôn đọc timestamptz thành giá trị UTC (offset 0), không phụ thuộc TimeZone của phiên (ADR-006).
        DateTimeOffset dbNow = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"").SingleAsync();
        Assert.Equal(TimeSpan.Zero, dbNow.Offset);
        Assert.InRange(dbNow, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public void Infrastructure_services_are_registered()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        Assert.Same(TimeProvider.System, services.GetRequiredService<TimeProvider>());
        Assert.NotNull(services.GetRequiredService<AuditableInterceptor>());
        Assert.False(string.IsNullOrEmpty(
            services.GetRequiredService<IOptions<server.Infrastructure.Options.DatabaseOptions>>().Value.DefaultConnection));
    }
}

/// <summary>Thiếu chuỗi kết nối ⇒ API phải từ chối khởi động (ValidateOnStart).</summary>
public class StartupValidationTests
{
    private sealed class MissingConnectionFactory : ApiFactory
    {
        protected override string? ConnectionStringOverride => "";
    }

    [Fact]
    public void Api_does_not_start_without_connection_string()
    {
        using var factory = new MissingConnectionFactory();

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("DefaultConnection", ex.ToString());
    }
}
