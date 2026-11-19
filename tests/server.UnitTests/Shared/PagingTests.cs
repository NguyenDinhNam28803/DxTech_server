using server.Shared.Paging;

namespace server.UnitTests.Shared;

public class PagingTests
{
    [Theory]
    [InlineData(1, 20, 1, 20, 0)]
    [InlineData(3, 10, 3, 10, 20)]
    [InlineData(0, 20, 1, 20, 0)]       // trang < 1 → trang 1
    [InlineData(-5, 20, 1, 20, 0)]
    [InlineData(2, 0, 2, 1, 1)]         // kích thước < 1 → 1
    [InlineData(1, 1000, 1, 100, 0)]    // vượt tối đa → 100
    public void Page_request_is_clamped(int page, int size, int expectedPage, int expectedSize, int expectedSkip)
    {
        var request = new PageRequest(page, size);

        Assert.Equal(expectedPage, request.Page);
        Assert.Equal(expectedSize, request.PageSize);
        Assert.Equal(expectedSkip, request.Skip);
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(1, 20, 1)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(105, 10, 11)]
    public void Total_pages_rounds_up(long total, int pageSize, int expected)
    {
        var page = new PagedResult<int>([], 1, pageSize, total);

        Assert.Equal(expected, page.TotalPages);
    }

    [Fact]
    public void Empty_page_echoes_request()
    {
        var page = PagedResult<string>.Empty(new PageRequest(4, 15));

        Assert.Empty(page.Items);
        Assert.Equal(4, page.Page);
        Assert.Equal(15, page.PageSize);
        Assert.Equal(0, page.Total);
    }
}
