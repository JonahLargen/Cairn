using Cairn;

namespace Cairn.Core.Tests;

public class EmbeddedResourceReportingTests
{
    [Fact]
    public void Reports_declared_embeds_with_their_child_type_and_arity()
    {
        var config = new LinkConfigRegistry().Add(new ParentLinks()).GetConfig(typeof(Parent));

        var reporting = Assert.IsAssignableFrom<IEmbeddedResourceReportingConfig>(config);
        var embeds = reporting.EmbeddedResources;

        Assert.Equal(2, embeds.Count);

        Assert.Equal("child", embeds[0].Relation.Value);
        Assert.Equal(typeof(Child), embeds[0].ResourceType);
        Assert.True(embeds[0].Single);

        Assert.Equal("watchers", embeds[1].Relation.Value);
        Assert.Equal(typeof(Child), embeds[1].ResourceType);
        Assert.False(embeds[1].Single);

        // The projection is memoized: a second read returns the same instance.
        Assert.Same(embeds, reporting.EmbeddedResources);
    }

    [Fact]
    public void A_config_with_no_embeds_reports_an_empty_list()
    {
        var config = new LinkConfigRegistry().Add(new ChildLinks()).GetConfig(typeof(Child));

        var reporting = Assert.IsAssignableFrom<IEmbeddedResourceReportingConfig>(config);

        Assert.Empty(reporting.EmbeddedResources);
        Assert.Same(reporting.EmbeddedResources, reporting.EmbeddedResources);
    }

    private sealed record Parent(int Id, Child Only, IReadOnlyList<Child> Many);

    private sealed record Child(int Id);

    private sealed class ParentLinks : LinkConfig<Parent>
    {
        public override void Configure(ILinkBuilder<Parent> builder)
        {
            builder.Self(p => LinkTarget.Uri($"/parent/{p.Id}"));
            builder.Embed("child", p => p.Only);
            builder.EmbedMany("watchers", p => p.Many);
        }
    }

    private sealed class ChildLinks : LinkConfig<Child>
    {
        public override void Configure(ILinkBuilder<Child> builder)
            => builder.Self(c => LinkTarget.Uri($"/child/{c.Id}"));
    }
}
