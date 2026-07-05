using Cairn.AspNetCore.Explorer;

namespace Cairn.AspNetCore.Explorer.Tests;

public class CairnExplorerOptionsTests
{
    [Fact]
    public void Defaults_are_the_conventional_ones()
    {
        var options = new CairnExplorerOptions();

        Assert.Equal("/explorer", options.Path);
        Assert.Equal("/", options.EntryPoint);
        Assert.Equal("Cairn HAL Explorer", options.Title);
        Assert.Null(options.Enabled);
    }

    [Fact]
    public void Accepts_valid_overrides()
    {
        var options = new CairnExplorerOptions
        {
            Path = "/browse",
            EntryPoint = "/api/v1",
            Title = "My API",
            Enabled = true,
        };

        Assert.Equal("/browse", options.Path);
        Assert.Equal("/api/v1", options.EntryPoint);
        Assert.Equal("My API", options.Title);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void Path_rejects_an_empty_value()
        => Assert.Throws<ArgumentException>(() => new CairnExplorerOptions { Path = "" });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EntryPoint_rejects_null_or_whitespace(string? value)
        => Assert.ThrowsAny<ArgumentException>(() => new CairnExplorerOptions { EntryPoint = value! });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_rejects_null_or_whitespace(string? value)
        => Assert.ThrowsAny<ArgumentException>(() => new CairnExplorerOptions { Title = value! });
}
