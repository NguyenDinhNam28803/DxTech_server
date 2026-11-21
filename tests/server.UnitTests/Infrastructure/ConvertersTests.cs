using server.Infrastructure.Persistence.Conversions;

namespace server.UnitTests.Infrastructure;

public class SnakeCaseEnumConverterTests
{
    public enum Sample
    {
        LicenseKey,
        Account,
        HTTPGateway,
    }

    [Theory]
    [InlineData(Sample.LicenseKey, "license_key")]
    [InlineData(Sample.Account, "account")]
    [InlineData(Sample.HTTPGateway, "http_gateway")]
    public void Round_trips_enum_values_as_snake_case(Sample value, string text)
    {
        Assert.Equal(text, SnakeCaseEnumConverter<Sample>.ToProvider(value));
        Assert.Equal(value, SnakeCaseEnumConverter<Sample>.FromProvider(text));
    }

    [Fact]
    public void Unknown_database_value_throws_instead_of_defaulting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SnakeCaseEnumConverter<Sample>.FromProvider("LicenseKey"));
        Assert.Contains("LicenseKey", ex.Message);
    }

    [Fact]
    public void Check_sql_lists_every_value()
    {
        Assert.Equal("kind IN ('license_key', 'account', 'http_gateway')", SnakeCaseEnumConverter<Sample>.CheckSql("kind"));
    }
}

public class JsonListConverterTests
{
    [Fact]
    public void Round_trips_unicode_list_as_json_array()
    {
        var list = new List<string> { "Kích hoạt online", "6TB OneDrive" };

        string json = (string)JsonListConverter.Converter.ConvertToProvider(list)!;
        var back = (List<string>)JsonListConverter.Converter.ConvertFromProvider(json)!;

        Assert.StartsWith("[", json);
        Assert.Equal(list, back);
    }

    [Fact]
    public void Comparer_detects_changes_inside_the_list()
    {
        var original = new List<string> { "a", "b" };
        List<string> snapshot = JsonListConverter.Comparer.Snapshot(original);

        original.Add("c");

        Assert.False(JsonListConverter.Comparer.Equals(original, snapshot));
        Assert.True(JsonListConverter.Comparer.Equals(new List<string> { "a" }, new List<string> { "a" }));
    }
}
