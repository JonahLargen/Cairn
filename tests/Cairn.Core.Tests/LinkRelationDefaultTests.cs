namespace Cairn.Core.Tests;

// default(LinkRelation) bypasses the constructor and carries a null Value — it must fail fast with a clear
// message wherever it enters the model, never as a NullReferenceException mid-serialization.
public class LinkRelationDefaultTests
{
    [Fact]
    public void A_default_relation_fails_fast_in_the_link_constructor()
    {
        var failure = Assert.Throws<ArgumentException>(() => new Link(default, "/orders/1"));
        Assert.Contains("default(LinkRelation)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_relation_fails_fast_in_the_affordance_constructor()
    {
        var failure = Assert.Throws<ArgumentException>(() => new Affordance(default, "/orders/1/cancel", "POST"));
        Assert.Contains("default(LinkRelation)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_relation_fails_at_config_registration_not_at_request_time()
    {
        var registry = new LinkConfigRegistry();

        var failure = Assert.Throws<ArgumentException>(() => registry.Add(new DefaultRelationConfig()));
        Assert.Contains("default(LinkRelation)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_of_a_default_relation_is_empty_not_null()
        => Assert.Equal(string.Empty, default(LinkRelation).ToString());

    private sealed record ConfigResource(int Id);

    private sealed class DefaultRelationConfig : LinkConfig<ConfigResource>
    {
        public override void Configure(ILinkBuilder<ConfigResource> builder)
            => builder.Link(default, r => LinkTarget.Uri($"/r/{r.Id}"));
    }
}
