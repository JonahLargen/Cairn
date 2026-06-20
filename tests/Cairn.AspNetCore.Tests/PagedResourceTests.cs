using Cairn.AspNetCore;

namespace Cairn.AspNetCore.Tests;

public class PagedResourceTests
{
    [Theory]
    [InlineData(25, 10, 3)]
    [InlineData(20, 10, 2)]
    [InlineData(0, 10, 0)]
    [InlineData(5, 0, 0)]
    public void Computes_total_pages(int totalCount, int pageSize, int expected)
    {
        var page = new PagedResource<int>([], Page: 1, pageSize, totalCount);

        Assert.Equal(expected, page.TotalPages);
    }
}
