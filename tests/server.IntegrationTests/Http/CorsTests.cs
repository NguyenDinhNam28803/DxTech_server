using System.Net;
using server.IntegrationTests.Infrastructure;

namespace server.IntegrationTests.Http;

/// <summary>
/// CORS qua pipeline thật (ADR-011). Môi trường Testing đọc appsettings.json:
/// chỉ cho phép origin https://dxtechs.com.
/// </summary>
public class CorsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string AllowedOrigin = "https://dxtechs.com";
    private const string ForeignOrigin = "https://evil.example";

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Allowed_origin_gets_cors_headers_with_credentials()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/health", AllowedOrigin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AllowedOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", response.Headers.Vary); // proxy/cache không được dùng chung response giữa các origin
    }

    [Fact]
    public async Task Foreign_origin_gets_no_cors_headers()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/health", ForeignOrigin);

        // Server vẫn trả lời, nhưng thiếu header ⇒ trình duyệt chặn trang lạ đọc response.
        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", response.Headers.Vary);
    }

    [Fact]
    public async Task Request_without_origin_still_varies_by_origin_and_has_single_vary_value()
    {
        HttpResponseMessage noOrigin = await _client.GetAsync("/api/health");
        HttpResponseMessage error = await SendAsync(HttpMethod.Get, "/api/__test/throw", AllowedOrigin);

        Assert.Contains("Origin", noOrigin.Headers.Vary);
        Assert.Single(error.Headers.Vary, v => v.Equals("Origin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Origin_with_different_scheme_or_subdomain_is_not_allowed()
    {
        Assert.Null(Header(await SendAsync(HttpMethod.Get, "/api/health", "http://dxtechs.com"), "Access-Control-Allow-Origin"));
        Assert.Null(Header(await SendAsync(HttpMethod.Get, "/api/health", "https://www.dxtechs.com"), "Access-Control-Allow-Origin"));
        Assert.Null(Header(await SendAsync(HttpMethod.Get, "/api/health", "https://dxtechs.com.evil.example"), "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_for_json_post_is_allowed_and_cached()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/__test/no-content");
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("POST", Header(response, "Access-Control-Allow-Methods"));
        Assert.Contains("content-type", Header(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorization", Header(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("600", Header(response, "Access-Control-Max-Age"));
        Assert.Contains("Origin", response.Headers.Vary);
    }

    [Fact]
    public async Task Preflight_from_foreign_origin_is_not_approved()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/__test/no-content");
        request.Headers.Add("Origin", ForeignOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Methods"));
    }

    [Theory]
    [InlineData("/api/__test/errors/NotFound", HttpStatusCode.NotFound)]
    [InlineData("/api/__test/throw", HttpStatusCode.InternalServerError)]
    public async Task Error_responses_keep_cors_headers_so_the_browser_can_read_problem_details(string path, HttpStatusCode expected)
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, AllowedOrigin);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AllowedOrigin, Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Location_header_is_exposed_to_scripts()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/health", AllowedOrigin);

        Assert.Contains("Location", Header(response, "Access-Control-Expose-Headers"));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string origin)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", origin);
        return _client.SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;
}

/// <summary>Cấu hình CORS sai ⇒ API phải từ chối khởi động.</summary>
public class CorsStartupValidationTests
{
    private sealed class CorsConfigFactory(string origin) : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Cors:AllowedOrigins:0", origin);
        }
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://dxtechs.com/")]
    [InlineData("http://dxtechs.com")]
    public void Api_does_not_start_with_unsafe_origin(string origin)
    {
        using var factory = new CorsConfigFactory(origin);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Cors:AllowedOrigins", ex.ToString());
    }
}
