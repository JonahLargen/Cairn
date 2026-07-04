namespace Cairn.Core.Tests;

// Build-stage edges the mainline engine tests don't reach: null multi-target sequences, the unresolved-link
// callback, the strict-mode description of each target kind, and the single-embed declaration.
public class LinkEngineEdgeTests
{
    [Fact]
    public async Task A_null_multi_target_sequence_contributes_no_links()
    {
        var set = await BuildAsync(new NullTargetsLinks(), new EdgeOrder(1));

        Assert.Empty(set.Links);
    }

    [Fact]
    public async Task Lax_mode_reports_a_dropped_link_through_the_callback()
    {
        UnresolvedLink? reported = null;
        var engine = new LinkEngine(new LinkConfigRegistry().Add(new RouteLinks()));
        var context = new LinkContext(new NeverResolves(), new AllowAllAuthorizer())
        {
            OnUnresolvedLink = unresolved => reported = unresolved,
        };

        var set = await engine.BuildAsync(new EdgeOrder(1), context);

        // The link is still dropped, but the host gets the details to log or meter the silent omission.
        Assert.Empty(set.Links);
        Assert.NotNull(reported);
        Assert.Equal(typeof(EdgeOrder), reported.ResourceType);
        Assert.Equal("self", reported.Relation.Value);
        Assert.IsType<RouteLinkTarget>(reported.Target);
    }

    [Fact]
    public async Task Strict_mode_message_names_the_route_template()
    {
        var context = Context(new NeverResolves(), LinkResolutionMode.Strict);
        var engine = new LinkEngine(new LinkConfigRegistry().Add(new RouteTemplateLinks()));

        var exception = await Assert.ThrowsAsync<LinkResolutionException>(
            async () => await engine.BuildAsync(new EdgeOrder(1), context));

        Assert.Contains("route template 'search-orders'", exception.Message);
    }

    [Fact]
    public async Task Strict_mode_message_names_the_explicit_uri()
    {
        var context = Context(new NeverResolves(), LinkResolutionMode.Strict);
        var engine = new LinkEngine(new LinkConfigRegistry().Add(new ExplicitUriLinks()));

        var exception = await Assert.ThrowsAsync<LinkResolutionException>(
            async () => await engine.BuildAsync(new EdgeOrder(1), context));

        Assert.Contains("URI '/orders/1'", exception.Message);
    }

    [Fact]
    public async Task Embed_includes_a_resolved_single_child()
    {
        var set = await BuildAsync(new SingleEmbedLinks(), new EdgeOrder(1));

        var embedded = Assert.Single(set.Embedded);
        Assert.Equal("customer", embedded.Relation.Value);
        Assert.Equal(new EdgeCustomer("Ada"), Assert.Single(embedded.Resources));
    }

    [Fact]
    public async Task Embed_skips_a_null_single_child_entirely()
    {
        var set = await BuildAsync(new NullSingleEmbedLinks(), new EdgeOrder(1));

        // Unlike EmbedMany (which keeps the relation with zero resources), a null single embed contributes
        // nothing — no empty _embedded entry on the wire.
        Assert.Empty(set.Embedded);
    }

    [Fact]
    public async Task EmbedMany_filters_null_items_out_of_the_sequence()
    {
        var set = await BuildAsync(new SparseEmbedLinks(), new EdgeOrder(1));

        var embedded = Assert.Single(set.Embedded);
        Assert.Equal(new object[] { new EdgeCustomer("Ada"), new EdgeCustomer("Grace") }, embedded.Resources);
    }

    private static async Task<LinkSet> BuildAsync(LinkConfig<EdgeOrder> config, EdgeOrder resource)
    {
        var engine = new LinkEngine(new LinkConfigRegistry().Add(config));
        return await engine.BuildAsync(resource, Context(new ExplicitUrlResolver()));
    }

    private static LinkContext Context(ILinkUrlResolver resolver, LinkResolutionMode mode = LinkResolutionMode.Lax)
        => new(resolver, new AllowAllAuthorizer(), mode);

    private sealed record EdgeOrder(int Id);

    private sealed record EdgeCustomer(string Name);

    private sealed class NullTargetsLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> b)
            => b.Links("mirrors", _ => null!);
    }

    private sealed class RouteLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> b)
            => b.Self(o => LinkTarget.Route("order", new { id = o.Id }));
    }

    private sealed class RouteTemplateLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> b)
            => b.Link("search", _ => LinkTarget.RouteTemplate("search-orders"));
    }

    private sealed class ExplicitUriLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> b)
            => b.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
    }

    private sealed class SingleEmbedLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> b)
            => b.Embed("customer", _ => new EdgeCustomer("Ada"));
    }

    private sealed class NullSingleEmbedLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> b)
            => b.Embed<EdgeCustomer>("customer", _ => null);
    }

    private sealed class SparseEmbedLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> b)
            => b.EmbedMany("customers", _ => new EdgeCustomer?[] { new("Ada"), null, new("Grace") });
    }

    private sealed class ExplicitUrlResolver : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => (target as ExplicitLinkTarget)?.Href;
    }

    private sealed class NeverResolves : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => null;
    }

    private sealed class AllowAllAuthorizer : ILinkAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default) => new(true);
    }
}
