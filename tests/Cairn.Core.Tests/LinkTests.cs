namespace Cairn.Core.Tests;

public class LinkTests
{
    [Fact]
    public void Constructing_a_self_link_sets_relation_and_href()
    {
        var link = new Link(IanaLinkRelations.Self, "/orders/42");

        Assert.Equal("self", link.Relation.Value);
        Assert.Equal("/orders/42", link.Href);
        Assert.False(link.Templated);
    }

    [Fact]
    public void Links_with_the_same_values_are_equal()
    {
        var a = new Link(IanaLinkRelations.Next, "/orders?page=2");
        var b = new Link(IanaLinkRelations.Next, "/orders?page=2");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Link_requires_a_non_empty_href()
    {
        Assert.Throws<ArgumentException>(() => new Link(IanaLinkRelations.Self, "   "));
    }

    [Fact]
    public void Templated_link_preserves_the_template()
    {
        var link = new Link("search", "/orders{?status,page}", templated: true);

        Assert.True(link.Templated);
        Assert.Equal("/orders{?status,page}", link.Href);
    }

    [Fact]
    public void LinkRelation_implicitly_converts_from_string()
    {
        LinkRelation rel = "edit";

        Assert.Equal("edit", rel.Value);
    }

    [Fact]
    public void LinkRelation_rejects_whitespace()
    {
        Assert.Throws<ArgumentException>(() => new LinkRelation("  "));
    }
}
