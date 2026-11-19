using System.Net;
using System.Text.Json;
using server.IntegrationTests.Infrastructure;

namespace server.IntegrationTests.Http;

/// <summary>Result → HTTP/ProblemDetails qua pipeline ASP.NET Core thật (ADR-004).</summary>
public class ProblemDetailsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("Unauthorized", HttpStatusCode.Unauthorized)]
    [InlineData("Forbidden", HttpStatusCode.Forbidden)]
    [InlineData("NotFound", HttpStatusCode.NotFound)]
    [InlineData("Conflict", HttpStatusCode.Conflict)]
    [InlineData("BusinessRule", HttpStatusCode.UnprocessableEntity)]
    public async Task Business_errors_map_to_status_and_problem_details(string type, HttpStatusCode expected)
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/__test/errors/{type}");
        JsonElement body = await ReadJsonAsync(response);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, body.GetProperty("status").GetInt32());
        Assert.Equal($"test.{type.ToLowerInvariant()}", body.GetProperty("code").GetString());
        Assert.Equal($"Lỗi kiểm thử {type}.", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("title").GetString()));
        Assert.True(body.TryGetProperty("traceId", out _)); // để đối chiếu với log
    }

    [Fact]
    public async Task Validation_error_lists_field_errors()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/__test/errors/Validation");
        JsonElement body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("test.invalid", body.GetProperty("code").GetString());
        Assert.Equal("Email không hợp lệ.", body.GetProperty("errors").GetProperty("email")[0].GetString());
    }

    [Fact]
    public async Task Success_returns_200_with_camel_case_json()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/__test/ok");
        JsonElement body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(690000, body.GetProperty("priceVnd").GetInt64());
    }

    [Fact]
    public async Task Success_without_value_returns_204()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/__test/no-content", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Unexpected_exception_returns_500_problem_without_leaking_details()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/__test/throw");
        string raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("bí mật nội bộ", raw);
    }

    [Fact]
    public async Task Unknown_route_returns_404_problem()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/khong-ton-tai");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
