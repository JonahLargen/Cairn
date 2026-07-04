namespace Cairn.Core.Tests;

// The fluent declaration surface that the mainline engine tests don't reach: link metadata setters,
// the context-taking condition overloads, per-spec authorization, and the remaining method shorthands.
public class LinkSpecFluentTests
{
    [Fact]
    public async Task A_link_title_flows_onto_the_built_link()
    {
        var set = await BuildAsync(new TitledLinks(), new SpecOrder(7, "Pending"));

        Assert.Equal("The order", Assert.Single(set.Links).Title);
    }

    [Fact]
    public async Task A_link_condition_can_observe_the_context()
    {
        var included = await BuildAsync(new ContextConditionLinks(), new SpecOrder(7, "Pending"), new FakeAuthorizer(true));
        var excluded = await BuildAsync(new ContextConditionLinks(), new SpecOrder(7, "Pending"), new FakeAuthorizer(true), LinkResolutionMode.Strict);

        Assert.Single(included.Links);
        Assert.Empty(excluded.Links);
    }

    [Fact]
    public async Task A_link_can_require_a_named_policy()
    {
        var authorizer = new FakeAuthorizer(false);

        var set = await BuildAsync(new PolicyLinks(), new SpecOrder(7, "Pending"), authorizer);

        Assert.Empty(set.Links);
        Assert.Contains("CanSeeAudit", authorizer.AskedPolicies);
    }

    [Fact]
    public async Task A_link_can_require_the_default_policy()
    {
        var authorizer = new FakeAuthorizer(true);

        var set = await BuildAsync(new DefaultPolicyLinks(), new SpecOrder(7, "Pending"), authorizer);

        Assert.Single(set.Links);
        Assert.Contains("", authorizer.AskedPolicies);
    }

    [Fact]
    public async Task Get_and_patch_shorthands_set_the_method()
    {
        var set = await BuildAsync(new MethodShorthandLinks(), new SpecOrder(7, "Pending"));

        Assert.Equal("GET", set.Affordances.Single(a => a.Name.Value == "peek").Method);
        Assert.Equal("PATCH", set.Affordances.Single(a => a.Name.Value == "amend").Method);
    }

    [Fact]
    public async Task An_affordance_title_flows_onto_the_built_affordance()
    {
        var set = await BuildAsync(new TitledAffordanceLinks(), new SpecOrder(7, "Pending"));

        Assert.Equal("Approve the order", Assert.Single(set.Affordances).Title);
    }

    [Fact]
    public async Task An_affordance_condition_can_observe_the_context()
    {
        var included = await BuildAsync(new ContextConditionAffordance(), new SpecOrder(7, "Pending"), new FakeAuthorizer(true));
        var excluded = await BuildAsync(new ContextConditionAffordance(), new SpecOrder(7, "Pending"), new FakeAuthorizer(true), LinkResolutionMode.Strict);

        Assert.Single(included.Affordances);
        Assert.Empty(excluded.Affordances);
    }

    [Fact]
    public async Task Self_accepts_an_async_target()
    {
        var set = await BuildAsync(new AsyncSelfLinks(), new SpecOrder(7, "Pending"));

        Assert.Equal("/orders/7", Assert.Single(set.Links).Href);
    }

    [Fact]
    public async Task EmbedMany_treats_a_null_sequence_as_empty()
    {
        var set = await BuildAsync(new NullEmbedLinks(), new SpecOrder(7, "Pending"));

        Assert.Empty(Assert.Single(set.Embedded).Resources);
    }

    private static async Task<LinkSet> BuildAsync(
        LinkConfig<SpecOrder> config,
        SpecOrder resource,
        ILinkAuthorizer? authorizer = null,
        LinkResolutionMode mode = LinkResolutionMode.Lax)
    {
        var engine = new LinkEngine(new LinkConfigRegistry().Add(config));
        var context = new LinkContext(new ExplicitUrlResolver(), authorizer ?? new FakeAuthorizer(true), mode);
        return await engine.BuildAsync(resource, context);
    }

    private sealed record SpecOrder(int Id, string Status);

    private sealed class TitledLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.Self(o => LinkTarget.Uri($"/orders/{o.Id}")).Title("The order");
    }

    private sealed class ContextConditionLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.Self(o => LinkTarget.Uri($"/orders/{o.Id}")).When((_, ctx) => ctx.Mode == LinkResolutionMode.Lax);
    }

    private sealed class PolicyLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.Link("audit", o => LinkTarget.Uri($"/orders/{o.Id}/audit")).RequireAuthorization("CanSeeAudit");
    }

    private sealed class DefaultPolicyLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.Link("audit", o => LinkTarget.Uri($"/orders/{o.Id}/audit")).RequireAuthorization();
    }

    private sealed class MethodShorthandLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
        {
            b.Affordance("peek", o => LinkTarget.Uri($"/orders/{o.Id}")).Get();
            b.Affordance("amend", o => LinkTarget.Uri($"/orders/{o.Id}")).Patch();
        }
    }

    private sealed class TitledAffordanceLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.Affordance("approve", o => LinkTarget.Uri($"/orders/{o.Id}/approve")).Title("Approve the order");
    }

    private sealed class ContextConditionAffordance : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.Affordance("approve", o => LinkTarget.Uri($"/orders/{o.Id}/approve")).When((_, ctx) => ctx.Mode == LinkResolutionMode.Lax);
    }

    private sealed class AsyncSelfLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.Self((o, _) => new ValueTask<LinkTarget>(LinkTarget.Uri($"/orders/{o.Id}")));
    }

    private sealed class NullEmbedLinks : LinkConfig<SpecOrder>
    {
        public override void Configure(ILinkBuilder<SpecOrder> b)
            => b.EmbedMany<SpecOrder>("children", _ => null);
    }

    private sealed class ExplicitUrlResolver : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => (target as ExplicitLinkTarget)?.Href;
    }

    private sealed class FakeAuthorizer(bool result) : ILinkAuthorizer
    {
        public List<string> AskedPolicies { get; } = [];

        public ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default)
        {
            AskedPolicies.Add(policy);
            return new ValueTask<bool>(result);
        }
    }
}
