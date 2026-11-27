using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Identity;
using server.Modules.Identity.Domain;
using server.Modules.Identity.Features.Admin;
using server.Shared.Results;

namespace server.IntegrationTests.Content;

/// <summary>
/// CT-1 (ADR-015 §4): role <c>editor</c> + lệnh <c>--set-role</c>. Editor không được vào API quản trị bán hàng;
/// đổi role làm token cũ mất quyền ngay; không thể hạ admin cuối cùng.
/// </summary>
public class EditorRoleTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Password = "Ngựa vằn chạy 42 vòng";

    public Task InitializeAsync() => TestDatabase.EnsureMigratedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Editor_token_carries_the_role_and_is_forbidden_from_sales_admin_api()
    {
        string email = await RegisterAsync("editor");
        Result<SetRoleResponse> set = await SetRole(email, "Editor");
        Assert.True(set.IsSuccess, set.Error?.Message);
        Assert.Equal(("customer", "editor", true), (set.Value.PreviousRole, set.Value.Role, set.Value.Changed));

        string token = await LoginAsync(email);
        HttpClient c = Client(token);
        using JsonDocument me = JsonDocument.Parse(await c.GetStringAsync("/api/me"));
        Assert.Equal("editor", me.RootElement.GetProperty("role").GetString());

        HttpResponseMessage payments = await c.GetAsync("/api/admin/payments/unresolved");
        Assert.Equal(HttpStatusCode.Forbidden, payments.StatusCode);
    }

    [Fact]
    public async Task Changing_role_rotates_the_stamp_so_an_old_admin_token_loses_access_at_once()
    {
        string email = await RegisterAsync("demote");
        Assert.True((await SetRole(email, "admin")).IsSuccess);
        string adminToken = await LoginAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await Client(adminToken).GetAsync("/api/admin/payments/unresolved")).StatusCode);
        Guid stampBefore = await StampAsync(email);

        Assert.True((await SetRole(email, "editor")).IsSuccess);

        Assert.NotEqual(stampBefore, await StampAsync(email));
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(adminToken).GetAsync("/api/admin/payments/unresolved")).StatusCode);

        // Nâng lại admin: role trong DB lại khớp claim của token cũ — chỉ còn security stamp phân biệt token cũ với phiên mới.
        Assert.True((await SetRole(email, "admin")).IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(adminToken).GetAsync("/api/admin/payments/unresolved")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(await LoginAsync(email)).GetAsync("/api/admin/payments/unresolved")).StatusCode);
    }

    [Fact]
    public async Task Same_role_is_a_no_op_and_bad_input_is_rejected()
    {
        string email = await RegisterAsync("noop");
        Guid stamp = await StampAsync(email);

        Result<SetRoleResponse> same = await SetRole(email.ToUpperInvariant(), "customer");
        Assert.True(same.IsSuccess);
        Assert.False(same.Value.Changed);
        Assert.Equal(stamp, await StampAsync(email));

        Assert.Equal("identity.invalid_role", (await SetRole(email, "superadmin")).Error.Code);
        Assert.Equal("identity.user_not_found", (await SetRole($"{TestDatabase.Unique("ghost")}@dxtechs.test", "editor")).Error.Code);
        Assert.Equal("identity.invalid_email", (await SetRole("not-an-email", "editor")).Error.Code);
    }

    [Fact]
    public async Task Command_line_prints_usage_and_result()
    {
        var output = new StringWriter();
        Assert.Equal(2, await IdentityModule.SetRoleAsync(factory.Services, ["--set-role", "a@b.c"], output));

        string email = await RegisterAsync("cli");
        output = new StringWriter();
        Assert.Equal(0, await IdentityModule.SetRoleAsync(factory.Services, ["--set-role", email, "editor"], output));
        Assert.Contains("customer → editor", output.ToString());

        output = new StringWriter();
        Assert.Equal(1, await IdentityModule.SetRoleAsync(factory.Services, ["--set-role", email, "root"], output));
        Assert.Contains("THẤT BẠI", output.ToString());
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted()
    {
        // Database riêng: đếm admin chính xác, không phụ thuộc admin của test khác.
        string database = $"roles_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(ApiFactory.ConnectionString);
        await using (var conn = new NpgsqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {database}", conn).ExecuteNonQueryAsync();
        }

        builder.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Add(new User("only@dxtechs.test", "Admin", new string('h', 40), UserRole.Admin));
                db.Add(new User("locked@dxtechs.test", "Khoá", new string('h', 40), UserRole.Admin) { Status = UserStatus.Locked });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options))
            {
                Result<SetRoleResponse> r = await new SetRoleHandler(db).HandleAsync(new SetRoleCommand("only@dxtechs.test", "editor"));
                Assert.Equal("identity.last_admin", r.Error.Code); // admin bị khoá không tính là "admin khác"
            }

            await using (var db = new AppDbContext(options))
            {
                db.Add(new User("second@dxtechs.test", "Admin 2", new string('h', 40), UserRole.Admin));
                await db.SaveChangesAsync();
                Assert.True((await new SetRoleHandler(db).HandleAsync(new SetRoleCommand("only@dxtechs.test", "editor"))).IsSuccess);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
            await conn.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", conn).ExecuteNonQueryAsync();
        }
    }

    // ---------- Hỗ trợ ----------

    private async Task<string> RegisterAsync(string prefix)
    {
        string email = $"{TestDatabase.Unique(prefix)}@dxtechs.test";
        (await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = Password, fullName = "Biên Tập" }))
            .EnsureSuccessStatusCode();
        return email;
    }

    private async Task<string> LoginAsync(string email)
    {
        HttpResponseMessage login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private HttpClient Client(string token)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<Result<SetRoleResponse>> SetRole(string email, string role)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SetRoleHandler>().HandleAsync(new SetRoleCommand(email, role));
    }

    private static async Task<Guid> StampAsync(string email)
    {
        await using AppDbContext db = TestDatabase.CreateContext();
        return await db.Set<User>().Where(u => u.Email == email).Select(u => u.SecurityStamp).SingleAsync();
    }
}
