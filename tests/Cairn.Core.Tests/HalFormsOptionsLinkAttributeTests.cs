namespace Cairn.Core.Tests;

public class HalFormsOptionsLinkAttributeTests
{
    [Fact]
    public void Constructing_the_attribute_sets_the_href_and_defaults_the_rest()
    {
        var attribute = new HalFormsOptionsLinkAttribute("/tags");

        Assert.Equal("/tags", attribute.Href);
        Assert.False(attribute.Templated);
        Assert.Null(attribute.Type);
        Assert.Null(attribute.ValueField);
        Assert.Null(attribute.PromptField);
    }

    [Fact]
    public void The_optional_metadata_round_trips_through_the_initializers()
    {
        var attribute = new HalFormsOptionsLinkAttribute("/tags{?q}")
        {
            Templated = true,
            Type = "application/json",
            ValueField = "id",
            PromptField = "name",
        };

        Assert.True(attribute.Templated);
        Assert.Equal("application/json", attribute.Type);
        Assert.Equal("id", attribute.ValueField);
        Assert.Equal("name", attribute.PromptField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_attribute_requires_a_non_empty_href(string? href)
    {
        Assert.Throws<ArgumentException>(() => new HalFormsOptionsLinkAttribute(href!));
    }
}
