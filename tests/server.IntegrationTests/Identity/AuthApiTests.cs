using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Modules.Identity.Features.Admin;
using server.Modules.Identity.Jobs;

namespace server.IntegrationTests.Identity;

/// <summary>
/// API xác thực (PR-8) trên PostgreSQL thật — các nhóm kiểm thử bắt buộc của ADR-012:
/// cookie, xoay vòng + phát hiện dùng lại, khoá tài khoản, Origin, phân quyền, lưu hash, giới hạn tần suất.
/// </summary>
public class AuthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Password = "Ngựa vằn chạy 42 vòng";
    private const string AllowedOrigin = "https://dxtechs.com";
    private const string CookieName = "__Secure-dxt_rt";
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => TestDatabase.EnsureMigratedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Đăng ký ----------

    [Fact]
    public async Task Register_returns_token_in_body_and_refresh_token_only_in_a_hardened_cookie()
    {
        string email = NewEmail();
        HttpResponseMessage response = await Register(email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/me", response.Headers.Location?.OriginalString);
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{CookieName}=", setCookie);
        foreach (string attribute in new[] { "httponly", "secure", "samesite=strict", "path=/api/auth", "expires=" })
        {
            Assert.Contains(attribute, setCookie, StringComparison.OrdinalIgnoreCase);
        }

        JsonElement body = await Json(response);
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.False(body.TryGetProperty("refreshToken", out _));          // refresh token không bao giờ nằm trong body
        Assert.Equal("customer", body.GetProperty("user").GetProperty("role").GetString());

        HttpResponseMessage me = await Get("/api/me", body.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(email, (await Json(me)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Register_rejects_duplicate_email_in_any_case_and_weak_passwords()
    {
        string email = NewEmail();
        Assert.Equal(HttpStatusCode.Created, (await Register(email)).StatusCode);

        HttpResponseMessage dup = await Register(email.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("auth.email_taken", (await Json(dup)).GetProperty("code").GetString());

        HttpResponseMessage weak = await Register(NewEmail(), "1234567890");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.True((await Json(weak)).GetProperty("errors").TryGetProperty("password", out _));
    }

    // ---------- Đăng nhập, khoá tạm ----------

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_error()
    {
        string email = NewEmail();
        await Register(email);

        HttpResponseMessage wrong = await Login(email, "mat khau sai hoan toan");
        HttpResponseMessage unknown = await Login(NewEmail(), Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("auth.invalid_credentials", (await Json(wrong)).GetProperty("code").GetString());
        Assert.Equal("auth.invalid_credentials", (await Json(unknown)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Five_failures_lock_the_account_then_a_successful_login_resets_the_counter()
    {
        string email = NewEmail();
        await Register(email);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, "sai mat khau " + i)).StatusCode);
        }

        HttpResponseMessage locked = await Login(email, Password); // đúng mật khẩu nhưng đang khoá
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        Assert.Equal("auth.locked", (await Json(locked)).GetProperty("code").GetString());

        await Sql($"UPDATE identity.users SET lockout_until = now() - interval '1 minute' WHERE email = '{email}'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, "sai lan nua")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(email, Password)).StatusCode);
        Assert.Equal("0", await Scalar($"SELECT failed_login_count::text FROM identity.users WHERE email = '{email}'"));
    }

    // ---------- Refresh: xoay vòng, ân hạn, phát hiện dùng lại ----------

    [Fact]
    public async Task Refresh_rotates_and_reuse_after_grace_revokes_the_whole_family()
    {
        string first = CookieOf(await Register(NewEmail()));

        HttpResponseMessage rotated = await Refresh(first);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        string second = CookieOf(rotated);
        Assert.NotEqual(first, second);

        // Trong 10 giây: coi như hai tab ⇒ 409, không thu hồi gì.
        HttpResponseMessage conflict = await Refresh(first);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("auth.refresh_conflict", (await Json(conflict)).GetProperty("code").GetString());

        // Sau thời gian ân hạn: dùng lại token cũ = bị đánh cắp ⇒ 401 và cả họ bị thu hồi, kể cả token mới.
        await Sql($"UPDATE identity.refresh_tokens SET revoked_at = now() - interval '1 minute' WHERE token_hash = {HashLiteral(first)}");
        HttpResponseMessage reused = await Refresh(first);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal("auth.refresh_reused", (await Json(reused)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(second)).StatusCode);
        Assert.Equal("reuse_detected", await Scalar($"SELECT revoked_reason FROM identity.refresh_tokens WHERE token_hash = {HashLiteral(second)}"));
    }

    [Fact]
    public async Task Database_stores_only_the_sha256_of_refresh_tokens()
    {
        string raw = CookieOf(await Register(NewEmail()));

        Assert.Equal("1", await Scalar($"SELECT count(*)::text FROM identity.refresh_tokens WHERE token_hash = {HashLiteral(raw)}"));
        Assert.Equal("0", await Scalar(
            $"SELECT count(*)::text FROM identity.refresh_tokens WHERE token_hash = convert_to('{raw}', 'UTF8') OR user_agent LIKE '%{raw}%'"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    public async Task Cookie_endpoints_require_an_allowed_origin(string? origin)
    {
        string raw = CookieOf(await Register(NewEmail()));

        foreach (string path in new[] { "/api/auth/refresh", "/api/auth/logout" })
        {
            HttpResponseMessage response = await PostWithCookie(path, raw, origin);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("auth.origin_not_allowed", (await Json(response)).GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, (await Refresh(raw)).StatusCode); // token vẫn còn nguyên
    }

    [Fact]
    public async Task Logout_revokes_the_token_and_clears_the_cookie()
    {
        string raw = CookieOf(await Register(NewEmail()));

        HttpResponseMessage logout = await PostWithCookie("/api/auth/logout", raw, AllowedOrigin);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=Thu, 01 Jan 1970", Assert.Single(logout.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(raw)).StatusCode);
    }

    // ---------- Bearer, phân quyền ----------

    [Fact]
    public async Task Protected_endpoints_return_problem_details_for_missing_or_forged_tokens()
    {
        HttpResponseMessage anonymous = await Get("/api/me", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("auth.unauthorized", (await Json(anonymous)).GetProperty("code").GetString());

        string token = (await Json(await Register(NewEmail()))).GetProperty("accessToken").GetString()!;
        string forged = token[..^4] + (token[^4..] == "AAAA" ? "BBBB" : "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/me", forged)).StatusCode);

        // Endpoint công khai và URL không tồn tại không bị ảnh hưởng.
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/categories", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/khong-ton-tai", null)).StatusCode);
    }

    [Fact]
    public async Task Admin_endpoint_checks_role_and_current_account_state()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/admin/ping", null)).StatusCode);

        string customer = (await Json(await Register(NewEmail()))).GetProperty("accessToken").GetString()!;
        HttpResponseMessage forbidden = await Get("/api/admin/ping", customer);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("auth.forbidden", (await Json(forbidden)).GetProperty("code").GetString());

        string adminEmail = NewEmail();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<CreateAdminHandler>()
                .HandleAsync(new CreateAdminCommand(adminEmail, "Quản Trị", Password))).IsSuccess);
        }

        string admin = (await Json(await Login(adminEmail, Password))).GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/admin/ping", admin)).StatusCode);

        // Bị khoá trong DB ⇒ mất quyền ở request kế tiếp, không chờ access token hết hạn.
        await Sql($"UPDATE identity.users SET status = 'locked' WHERE email = '{adminEmail}'");
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/admin/ping", admin)).StatusCode);
    }

    [Fact]
    public async Task Change_password_revokes_other_sessions_and_logout_all_kills_admin_tokens()
    {
        string email = NewEmail();
        HttpResponseMessage registered = await Register(email);
        string otherDevice = CookieOf(registered);
        string token = (await Json(registered)).GetProperty("accessToken").GetString()!;

        HttpResponseMessage changed = await Send(HttpMethod.Put, "/api/me/password", token,
            new { currentPassword = Password, newPassword = "Mật khẩu mới dài hơn 2026" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Refresh(CookieOf(changed))).StatusCode);      // thiết bị này: phiên mới
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(otherDevice)).StatusCode);    // thiết bị khác: bị thu hồi
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(email, "Mật khẩu mới dài hơn 2026")).StatusCode);

        HttpResponseMessage wrongCurrent = await Send(HttpMethod.Put, "/api/me/password", token,
            new { currentPassword = "khong dung dau nhe", newPassword = "Mật khẩu khác nữa 2026" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);

        // Đăng xuất mọi nơi: admin đổi security stamp ⇒ access token cũ mất quyền admin ngay.
        string adminEmail = NewEmail();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CreateAdminHandler>().HandleAsync(new CreateAdminCommand(adminEmail, "A", Password));
        }

        HttpResponseMessage adminLogin = await Login(adminEmail, Password);
        string adminToken = (await Json(adminLogin)).GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, "/api/me/logout-all", adminToken, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/admin/ping", adminToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(CookieOf(adminLogin))).StatusCode);
    }

    // ---------- DB: xoá tài khoản, dọn token ----------

    [Fact]
    public async Task Deleting_a_user_with_rotated_tokens_works_and_cleanup_removes_only_old_tokens()
    {
        string email = NewEmail();
        string raw = CookieOf(await Register(email));
        string rotated = CookieOf(await Refresh(raw));
        CookieOf(await Refresh(rotated));

        await Sql($"DELETE FROM identity.users WHERE email = '{email}'"); // RESTRICT cũ sẽ lỗi ở đây
        Assert.Equal("0", await Scalar($"SELECT count(*)::text FROM identity.refresh_tokens WHERE token_hash = {HashLiteral(raw)}"));

        string keep = CookieOf(await Register(NewEmail()));
        string old = CookieOf(await Register(NewEmail()));
        await Sql($"""
            UPDATE identity.refresh_tokens
               SET created_at = now() - interval '60 days', expires_at = now() - interval '40 days', session_expires_at = now() - interval '40 days'
             WHERE token_hash = {HashLiteral(old)}
            """);

        int deleted = await factory.Services.GetRequiredService<RefreshTokenCleanup>().RunOnceAsync(CancellationToken.None);
        Assert.True(deleted >= 1);
        Assert.Equal("0", await Scalar($"SELECT count(*)::text FROM identity.refresh_tokens WHERE token_hash = {HashLiteral(old)}"));
        Assert.Equal("1", await Scalar($"SELECT count(*)::text FROM identity.refresh_tokens WHERE token_hash = {HashLiteral(keep)}"));
    }

    // ---------- helpers ----------

    private static string NewEmail() => $"{TestDatabase.Unique("auth")}@dxtechs.test";

    private Task<HttpResponseMessage> Register(string email, string password = Password)
        => _client.PostAsJsonAsync("/api/auth/register", new { email, password, fullName = "Khách Thử" });

    private Task<HttpResponseMessage> Login(string email, string password)
        => _client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private Task<HttpResponseMessage> Refresh(string raw) => PostWithCookie("/api/auth/refresh", raw, AllowedOrigin);

    private Task<HttpResponseMessage> PostWithCookie(string path, string raw, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"{CookieName}={raw}");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Get(string path, string? token) => Send(HttpMethod.Get, path, token, null);

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private static string CookieOf(HttpResponseMessage response)
    {
        string header = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(CookieName + "="));
        return header[(CookieName.Length + 1)..header.IndexOf(';')];
    }

    private static string HashLiteral(string raw)
        => $"'\\x{Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(raw))).ToLowerInvariant()}'::bytea";

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task Sql(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> Scalar(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }
}

/// <summary>Giới hạn tần suất (ADR-012 §3) với giới hạn nhỏ để kiểm tra được 429.</summary>
public class AuthRateLimitTests
{
    private sealed class TightLimits : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:RateLimits:LoginPerMinute", "3");
        }
    }

    [Fact]
    public async Task Fourth_login_in_a_minute_gets_429_with_retry_after_and_other_endpoints_are_unaffected()
    {
        await TestDatabase.EnsureMigratedAsync();
        using var factory = new TightLimits();
        HttpClient client = factory.CreateClient();
        var body = new { email = "khong-ton-tai@dxtechs.test", password = "bat ky mat khau nao" };

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", body)).StatusCode);
        }

        HttpResponseMessage limited = await client.PostAsJsonAsync("/api/auth/login", body);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        string limitedBody = await limited.Content.ReadAsStringAsync();
        Assert.Contains("auth.rate_limited", limitedBody);
        Assert.Contains("\"title\":\"Too Many Requests\"", limitedBody);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/categories")).StatusCode);
    }
}

/// <summary>API không khởi động khi thiếu khoá ký hoặc khoá ngắn (ADR-012 §1).</summary>
public class AuthStartupValidationTests
{
    private sealed class KeyFactory(string key) : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:SigningKeys:0", key);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("qua-ngan-chi-20-bytes")]
    public void Api_refuses_to_start_without_a_strong_signing_key(string key)
    {
        using var factory = new KeyFactory(key);
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Auth:SigningKeys", ex.Message);
    }
}
