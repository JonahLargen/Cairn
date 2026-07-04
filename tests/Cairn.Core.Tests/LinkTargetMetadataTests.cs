namespace Cairn.Core.Tests;

// The per-target metadata copies (LinkTarget.With*) and how they override the spec-level values when the
// engine builds the link.
public class LinkTargetMetadataTests
{
    [Fact]
    public void WithType_returns_a_copy_with_the_media_type_set()
    {
        var target = LinkTarget.Uri("/orders/1").WithType("application/pdf");

        Assert.Equal("application/pdf", target.Type);
    }

    [Fact]
    public void WithDeprecation_returns_a_copy_with_the_deprecation_url_set()
    {
        var target = LinkTarget.Uri("/orders/1").WithDeprecation("https://example.test/gone");

        Assert.Equal("https://example.test/gone", target.Deprecation);
    }

    [Fact]
    public void WithProfile_returns_a_copy_with_the_profile_uri_set()
    {
        var target = LinkTarget.Uri("/orders/1").WithProfile("https://example.test/profile");

        Assert.Equal("https://example.test/profile", target.Profile);
    }

    [Fact]
    public async Task Per_target_metadata_flows_onto_the_built_link()
    {
        var engine = new LinkEngine(new LinkConfigRegistry().Add(new DecoratedTargetLinks()));
        var context = new LinkContext(new ExplicitUrlResolver(), new AllowAllAuthorizer());

        var set = await engine.BuildAsync(new MetaOrder(9), context);

        // The per-target attributes override the (unset) spec-level ones.
        var link = Assert.Single(set.Links);
        Assert.Equal("PDF invoice", link.Title);
        Assert.Equal("application/pdf", link.Type);
        Assert.Equal("pdf", link.Name);
        Assert.Equal("en", link.Hreflang);
        Assert.Equal("https://example.test/gone", link.Deprecation);
        Assert.Equal("https://example.test/profile", link.Profile);
    }

    private sealed record MetaOrder(int Id);

    private sealed class DecoratedTargetLinks : LinkConfig<MetaOrder>
    {
        public override void Configure(ILinkBuilder<MetaOrder> b)
            => b.Link("invoice", o => LinkTarget.Uri($"/orders/{o.Id}/invoice")
                .WithTitle("PDF invoice")
                .WithType("application/pdf")
                .WithName("pdf")
                .WithHreflang("en")
                .WithDeprecation("https://example.test/gone")
                .WithProfile("https://example.test/profile"));
    }

    private sealed class ExplicitUrlResolver : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => (target as ExplicitLinkTarget)?.Href;
    }

    private sealed class AllowAllAuthorizer : ILinkAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default) => new(true);
    }
}
