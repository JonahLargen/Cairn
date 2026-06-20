using Cairn;

namespace Cairn.Core.Tests;

public class LinkEngineTests
{
    [Fact]
    public async Task Emits_self_link_with_resolved_href()
    {
        var engine = EngineFor(new TestOrderLinks());

        var set = await engine.BuildAsync(new TestOrder(42, "Pending"), Context());

        var self = set.Links.Single(l => l.Relation.Value == "self");
        Assert.Equal("/orders/42", self.Href);
    }

    [Fact]
    public async Task Includes_conditional_link_when_predicate_holds()
    {
        var engine = EngineFor(new TestOrderLinks());

        var set = await engine.BuildAsync(new TestOrder(42, "Pending"), Context());

        Assert.Contains(set.Links, l => l.Relation.Value == "related");
    }

    [Fact]
    public async Task Excludes_conditional_link_when_predicate_fails()
    {
        var engine = EngineFor(new TestOrderLinks());

        var set = await engine.BuildAsync(new TestOrder(0, "Pending"), Context());

        Assert.DoesNotContain(set.Links, l => l.Relation.Value == "related");
    }

    [Fact]
    public async Task Includes_affordance_when_state_and_authorization_pass()
    {
        var engine = EngineFor(new TestOrderLinks());

        var set = await engine.BuildAsync(new TestOrder(42, "Pending"), Context(authorizer: new FakeAuthorizer(true)));

        var cancel = Assert.Single(set.Affordances);
        Assert.Equal("cancel", cancel.Name.Value);
        Assert.Equal("POST", cancel.Method);
        Assert.Equal("/orders/42/cancel", cancel.Href);
    }

    [Fact]
    public async Task Excludes_affordance_when_authorization_fails()
    {
        var engine = EngineFor(new TestOrderLinks());

        var set = await engine.BuildAsync(new TestOrder(42, "Pending"), Context(authorizer: new FakeAuthorizer(false)));

        Assert.Empty(set.Affordances);
    }

    [Fact]
    public async Task State_condition_short_circuits_before_authorization()
    {
        var authorizer = new FakeAuthorizer(true);
        var engine = EngineFor(new TestOrderLinks());

        var set = await engine.BuildAsync(new TestOrder(42, "Shipped"), Context(authorizer: authorizer));

        Assert.Empty(set.Affordances);
        Assert.Empty(authorizer.AskedPolicies);
    }

    [Fact]
    public async Task Affordance_defaults_to_post()
    {
        var engine = EngineFor(new DefaultMethodAffordance());

        var set = await engine.BuildAsync(new TestOrder(5, "Pending"), Context());

        Assert.Equal("POST", Assert.Single(set.Affordances).Method);
    }

    [Fact]
    public async Task Lax_mode_omits_unresolved_links()
    {
        var engine = EngineFor(new RouteSelfLink());

        var set = await engine.BuildAsync(new TestOrder(1, "Pending"), Context(resolver: new FakeUrlResolver(_ => null)));

        Assert.Empty(set.Links);
    }

    [Fact]
    public async Task Strict_mode_throws_on_unresolved_link()
    {
        var engine = EngineFor(new RouteSelfLink());
        var context = Context(resolver: new FakeUrlResolver(_ => null), mode: LinkResolutionMode.Strict);

        await Assert.ThrowsAsync<LinkResolutionException>(
            async () => await engine.BuildAsync(new TestOrder(1, "Pending"), context));
    }

    [Fact]
    public async Task Returns_empty_when_no_config_is_registered()
    {
        var engine = new LinkEngine(new LinkConfigRegistry());

        var set = await engine.BuildAsync(new TestOrder(1, "Pending"), Context());

        Assert.True(set.IsEmpty);
    }

    [Fact]
    public async Task Returns_empty_for_null_resource()
    {
        var engine = EngineFor(new TestOrderLinks());

        var set = await engine.BuildAsync<TestOrder?>(null, Context());

        Assert.True(set.IsEmpty);
    }

    private static LinkEngine EngineFor<T>(LinkConfig<T> config) => new(new LinkConfigRegistry().Add(config));

    private static LinkContext Context(
        ILinkUrlResolver? resolver = null,
        ILinkAuthorizer? authorizer = null,
        LinkResolutionMode mode = LinkResolutionMode.Lax)
        => new(resolver ?? new FakeUrlResolver(), authorizer ?? new FakeAuthorizer(true), mode);

    private sealed record TestOrder(int Id, string Status);

    private sealed class TestOrderLinks : LinkConfig<TestOrder>
    {
        public override void Configure(ILinkBuilder<TestOrder> b)
        {
            b.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
            b.Link("related", o => LinkTarget.Uri($"/orders/{o.Id}/related")).When(o => o.Id > 0);
            b.Affordance("cancel", o => LinkTarget.Uri($"/orders/{o.Id}/cancel"))
                .Method("POST")
                .When(o => o.Status == "Pending")
                .RequireAuthorization("CanCancel");
        }
    }

    private sealed class DefaultMethodAffordance : LinkConfig<TestOrder>
    {
        public override void Configure(ILinkBuilder<TestOrder> b)
            => b.Affordance("archive", o => LinkTarget.Uri($"/orders/{o.Id}/archive"));
    }

    private sealed class RouteSelfLink : LinkConfig<TestOrder>
    {
        public override void Configure(ILinkBuilder<TestOrder> b)
            => b.Self(o => LinkTarget.Route("order", new { id = o.Id }));
    }

    private sealed class FakeUrlResolver(Func<LinkTarget, string?>? resolve = null) : ILinkUrlResolver
    {
        private readonly Func<LinkTarget, string?> _resolve = resolve ?? Default;

        public string? Resolve(LinkTarget target) => _resolve(target);

        private static string? Default(LinkTarget target) => target switch
        {
            ExplicitLinkTarget e => e.Href,
            _ => null,
        };
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
