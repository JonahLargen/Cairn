using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Cairn.Core.Tests;

// Property-based tests: the engine is driven with randomized resources and randomized link data,
// checking invariants that must hold no matter what values a DTO carries.
[Properties(Arbitrary = [typeof(PropertyTestArbitraries)])]
public class LinkEnginePropertyTests
{
    [Property]
    public bool Self_href_reflects_the_resource_for_any_id(int id, RelationToken status)
    {
        var set = Build(new TestOrderLinks(), new TestOrder(id, status.Value));

        var self = set.Links.Single(l => l.Relation == new LinkRelation("self"));
        return self.Href == $"/orders/{id}";
    }

    [Property]
    public bool Conditional_link_appears_exactly_when_its_predicate_holds(int id, RelationToken status)
    {
        var set = Build(new TestOrderLinks(), new TestOrder(id, status.Value));

        return set.Links.Any(l => l.Relation == new LinkRelation("related")) == (id > 0);
    }

    [Property]
    public bool State_gated_affordance_appears_exactly_when_the_state_matches(int id, RelationToken status)
    {
        var set = Build(new TestOrderLinks(), new TestOrder(id, status.Value));

        return set.Affordances.Any(a => a.Name == new LinkRelation("cancel")) == (status.Value == "Pending");
    }

    [Property]
    public bool Emitted_links_never_carry_an_empty_relation_or_href(int id, RelationToken status)
    {
        var set = Build(new TestOrderLinks(), new TestOrder(id, status.Value));

        return set.Links.All(l => !string.IsNullOrWhiteSpace(l.Relation.Value) && !string.IsNullOrWhiteSpace(l.Href))
            && set.Affordances.All(a => !string.IsNullOrWhiteSpace(a.Name.Value) && !string.IsNullOrWhiteSpace(a.Href));
    }

    [Property]
    public bool Lax_mode_drops_unresolved_links_without_throwing(int id, RelationToken status)
    {
        var set = Build(new TestOrderLinks(), new TestOrder(id, status.Value), new NeverResolves());

        return set.Links.Count == 0;
    }

    [Property]
    public bool Strict_mode_always_surfaces_an_unresolved_link(int id, RelationToken status)
    {
        var context = new LinkContext(new NeverResolves(), new AllowAll(), LinkResolutionMode.Strict);
        var engine = new LinkEngine(new LinkConfigRegistry().Add(new TestOrderLinks()));

        try
        {
            _ = engine.BuildAsync(new TestOrder(id, status.Value), context).AsTask().GetAwaiter().GetResult();
            return false;
        }
        catch (LinkResolutionException)
        {
            return true;
        }
    }

    [Property]
    public bool A_dynamically_declared_relation_round_trips_through_the_engine(RelationToken relation)
    {
        // "self" is declared by the config too; any other relation must come back verbatim.
        var set = Build(new DynamicRelationLinks(relation.Value), new TestOrder(1, "x"));

        return set.Links.Count(l => l.Relation == new LinkRelation(relation.Value)) >= 1
            && set.Links.All(l => l.Href == "/fixed");
    }

    private static LinkSet Build<T>(LinkConfig<T> config, T resource, ILinkUrlResolver? resolver = null)
    {
        var engine = new LinkEngine(new LinkConfigRegistry().Add(config));
        var context = new LinkContext(resolver ?? new EchoResolver(), new AllowAll());
        return engine.BuildAsync(resource!, context).AsTask().GetAwaiter().GetResult();
    }

    private sealed record TestOrder(int Id, string Status);

    private sealed class TestOrderLinks : LinkConfig<TestOrder>
    {
        public override void Configure(ILinkBuilder<TestOrder> b)
        {
            b.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
            b.Link("related", o => LinkTarget.Uri($"/orders/{o.Id}/related")).When(o => o.Id > 0);
            b.Affordance("cancel", o => LinkTarget.Uri($"/orders/{o.Id}/cancel")).When(o => o.Status == "Pending");
        }
    }

    private sealed class DynamicRelationLinks(string relation) : LinkConfig<TestOrder>
    {
        public override void Configure(ILinkBuilder<TestOrder> b)
        {
            b.Self(_ => LinkTarget.Uri("/fixed"));
            b.Link(relation, _ => LinkTarget.Uri("/fixed"));
        }
    }

    private sealed class EchoResolver : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => target is ExplicitLinkTarget e ? e.Href : null;
    }

    private sealed class NeverResolves : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => null;
    }

    private sealed class AllowAll : ILinkAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default)
            => new(true);
    }
}
