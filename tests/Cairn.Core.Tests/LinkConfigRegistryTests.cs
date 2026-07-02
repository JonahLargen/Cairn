using Cairn;

namespace Cairn.Core.Tests;

public class LinkConfigRegistryTests
{
    [Fact]
    public void Base_type_config_covers_a_derived_type()
    {
        var registry = new LinkConfigRegistry().Add(new BaseOrderLinks());

        Assert.Same(registry.GetConfig(typeof(BaseOrder)), registry.GetConfig(typeof(RushOrder)));
    }

    [Fact]
    public async Task Base_type_config_builds_links_for_a_derived_instance()
    {
        var engine = new LinkEngine(new LinkConfigRegistry().Add(new BaseOrderLinks()));

        var set = await engine.BuildAsync(new RushOrder(9), Context());

        Assert.Equal("/orders/9", set.Links.Single(l => l.Relation.Value == "self").Href);
    }

    [Fact]
    public void Exact_type_config_wins_over_a_base_type_config()
    {
        var registry = new LinkConfigRegistry()
            .Add(new BaseOrderLinks())
            .Add(new RushOrderLinks());

        Assert.NotSame(registry.GetConfig(typeof(BaseOrder)), registry.GetConfig(typeof(RushOrder)));
    }

    [Fact]
    public void The_nearest_registered_base_type_wins()
    {
        var registry = new LinkConfigRegistry()
            .Add(new BaseOrderLinks())
            .Add(new RushOrderLinks());

        Assert.Same(registry.GetConfig(typeof(RushOrder)), registry.GetConfig(typeof(SameDayRushOrder)));
    }

    [Fact]
    public void Registering_a_config_invalidates_prior_resolutions()
    {
        var registry = new LinkConfigRegistry().Add(new BaseOrderLinks());
        _ = registry.GetConfig(typeof(RushOrder));   // resolves (and caches) the base config

        registry.Add(new RushOrderLinks());

        Assert.NotSame(registry.GetConfig(typeof(BaseOrder)), registry.GetConfig(typeof(RushOrder)));
    }

    [Fact]
    public void Unregistered_hierarchy_resolves_to_null()
    {
        var registry = new LinkConfigRegistry().Add(new BaseOrderLinks());

        Assert.Null(registry.GetConfig(typeof(string)));
    }

    private static LinkContext Context()
        => new(new ExplicitUrlResolver(), new AllowAllAuthorizer(), LinkResolutionMode.Lax);

    private record BaseOrder(int Id);

    private record RushOrder(int Id) : BaseOrder(Id);

    private sealed record SameDayRushOrder(int Id) : RushOrder(Id);

    private sealed class BaseOrderLinks : LinkConfig<BaseOrder>
    {
        public override void Configure(ILinkBuilder<BaseOrder> b)
            => b.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
    }

    private sealed class RushOrderLinks : LinkConfig<RushOrder>
    {
        public override void Configure(ILinkBuilder<RushOrder> b)
            => b.Self(o => LinkTarget.Uri($"/rush-orders/{o.Id}"));
    }

    private sealed class ExplicitUrlResolver : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => target is ExplicitLinkTarget e ? e.Href : null;
    }

    private sealed class AllowAllAuthorizer : ILinkAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default)
            => new(true);
    }
}
