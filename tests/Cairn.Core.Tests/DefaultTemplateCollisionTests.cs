using Cairn;

namespace Cairn.Core.Tests;

public class DefaultTemplateCollisionTests
{
    [Fact]
    public void Two_AsDefault_affordances_throw_at_registration()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkConfigRegistry().Add(new TwoDefaultsLinks()));

        // Both would emit under the reserved "default" HAL-FORMS template key, last-wins — fail loudly instead.
        Assert.Contains("default", exception.Message);
        Assert.Contains("approve", exception.Message);
        Assert.Contains("reject", exception.Message);
    }

    [Fact]
    public void AsDefault_plus_an_affordance_named_default_throws_at_registration()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkConfigRegistry().Add(new DefaultNameClashLinks()));

        Assert.Contains("approve", exception.Message);
    }

    [Fact]
    public void A_single_AsDefault_affordance_registers_fine()
    {
        var registry = new LinkConfigRegistry().Add(new SingleDefaultLinks());

        Assert.NotNull(registry.GetConfig(typeof(CollisionOrder)));
    }

    [Fact]
    public void An_affordance_named_default_without_AsDefault_registers_fine()
    {
        var registry = new LinkConfigRegistry().Add(new NamedDefaultLinks());

        Assert.NotNull(registry.GetConfig(typeof(CollisionOrder)));
    }

    private sealed record CollisionOrder(int Id);

    private sealed class TwoDefaultsLinks : LinkConfig<CollisionOrder>
    {
        public override void Configure(ILinkBuilder<CollisionOrder> builder)
        {
            builder.Affordance("approve", order => LinkTarget.Uri($"/orders/{order.Id}/approve")).AsDefault();
            builder.Affordance("reject", order => LinkTarget.Uri($"/orders/{order.Id}/reject")).AsDefault();
        }
    }

    private sealed class DefaultNameClashLinks : LinkConfig<CollisionOrder>
    {
        public override void Configure(ILinkBuilder<CollisionOrder> builder)
        {
            builder.Affordance("approve", order => LinkTarget.Uri($"/orders/{order.Id}/approve")).AsDefault();

            // "Default" collides case-insensitively with the reserved template key the AsDefault claims.
            builder.Affordance("Default", order => LinkTarget.Uri($"/orders/{order.Id}"));
        }
    }

    private sealed class SingleDefaultLinks : LinkConfig<CollisionOrder>
    {
        public override void Configure(ILinkBuilder<CollisionOrder> builder)
            => builder.Affordance("approve", order => LinkTarget.Uri($"/orders/{order.Id}/approve")).AsDefault();
    }

    private sealed class NamedDefaultLinks : LinkConfig<CollisionOrder>
    {
        public override void Configure(ILinkBuilder<CollisionOrder> builder)
            => builder.Affordance("default", order => LinkTarget.Uri($"/orders/{order.Id}"));
    }
}
