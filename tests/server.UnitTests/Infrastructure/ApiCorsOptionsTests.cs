using System.ComponentModel.DataAnnotations;
using server.Infrastructure.Options;

namespace server.UnitTests.Infrastructure;

public class ApiCorsOptionsTests
{
    [Theory]
    [InlineData("https://dxtechs.com")]
    [InlineData("https://www.dxtechs.com")]
    [InlineData("https://dxtechs.com:8443")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:4173")]
    public void Accepts_exact_origins(string origin)
    {
        Assert.True(ApiCorsOptions.IsValidOrigin(origin, out string reason), reason);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.dxtechs.com")]
    [InlineData("https://dxtechs.com/")]          // dấu '/' cuối: trình duyệt gửi Origin không có '/', sẽ không khớp
    [InlineData("https://dxtechs.com/app")]
    [InlineData("https://dxtechs.com?x=1")]
    [InlineData("http://dxtechs.com")]            // http ngoài localhost
    [InlineData("ftp://dxtechs.com")]
    [InlineData("dxtechs.com")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_unsafe_or_malformed_origins(string origin)
    {
        Assert.False(ApiCorsOptions.IsValidOrigin(origin, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Validation_reports_each_bad_origin_and_requires_at_least_one()
    {
        var empty = new ApiCorsOptions { AllowedOrigins = [] };
        var mixed = new ApiCorsOptions { AllowedOrigins = ["https://dxtechs.com", "*", "https://x.com/path"] };

        Assert.NotEmpty(Validate(empty));
        List<ValidationResult> errors = Validate(mixed);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.ErrorMessage!.Contains("'*'"));
        Assert.Contains(errors, e => e.ErrorMessage!.Contains("https://x.com/path"));
    }

    [Fact]
    public void Validation_passes_for_production_config()
    {
        Assert.Empty(Validate(new ApiCorsOptions { AllowedOrigins = ["https://dxtechs.com"] }));
    }

    private static List<ValidationResult> Validate(ApiCorsOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
