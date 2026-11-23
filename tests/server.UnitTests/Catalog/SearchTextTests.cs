using server.Modules.Catalog.Features.Products;

namespace server.UnitTests.Catalog;

public class SearchTextTests
{
    [Theory]
    [InlineData("phan mem", "phan:* & mem:*")]
    [InlineData("  Phần   MỀM  ", "phần:* & mềm:*")]
    [InlineData("đồ-họa!!", "đồ:* & họa:*")]
    [InlineData("windows 11", "windows:* & 11:*")]
    [InlineData("office office", "office:*")]
    public void Builds_prefix_and_query(string input, string expected)
    {
        Assert.Equal(expected, SearchText.ToTsQuery(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&|!:*()'")]
    public void Returns_null_when_there_is_no_word(string? input)
    {
        Assert.Null(SearchText.ToTsQuery(input));
    }

    [Fact]
    public void Tsquery_operators_and_quotes_in_input_cannot_reach_the_query()
    {
        string result = SearchText.ToTsQuery("a' | b) & !c:* ; DROP TABLE x")!;

        Assert.Equal("a:* & b:* & c:* & drop:* & table:* & x:*", result);
    }

    [Fact]
    public void Limits_number_and_length_of_terms()
    {
        string many = string.Join(' ', Enumerable.Range(1, 20).Select(i => $"w{i}"));
        string longWord = new('a', 100);

        Assert.Equal(SearchText.MaxTerms, SearchText.ToTsQuery(many)!.Split(" & ").Length);
        Assert.Equal(SearchText.MaxTermLength + 2, SearchText.ToTsQuery(longWord)!.Length);
    }
}
