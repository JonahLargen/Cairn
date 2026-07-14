using Cairn;

namespace Cairn.Core.Tests;

public class DeclarationReportingTests
{
    [Fact]
    public void Reports_declared_links_with_their_static_metadata()
    {
        var config = new LinkConfigRegistry().Add(new OrderLinks()).GetConfig(typeof(Order));

        var reporting = Assert.IsAssignableFrom<IDeclarationReportingConfig>(config);
        var links = reporting.DeclaredLinks;

        Assert.Equal(4, links.Count);

        Assert.Equal("self", links[0].Relation.Value);
        Assert.Null(links[0].Title);
        Assert.False(links[0].Multi);
        Assert.False(links[0].Conditional);

        Assert.Equal("customer", links[1].Relation.Value);
        Assert.Equal("The customer", links[1].Title);
        Assert.Equal("application/json", links[1].Type);
        Assert.Equal("primary", links[1].Name);
        Assert.Equal("https://docs.example.com/gone", links[1].Deprecation);
        Assert.Equal("en-US", links[1].Hreflang);
        Assert.Equal("https://schemas.example.com/customer", links[1].Profile);
        Assert.False(links[1].Multi);

        Assert.Equal("item", links[2].Relation.Value);
        Assert.True(links[2].Multi);

        // A When-gated link is conditional, just like a policy-gated one.
        Assert.Equal("receipt", links[3].Relation.Value);
        Assert.True(links[3].Conditional);

        // The projection is memoized: a second read returns the same instance.
        Assert.Same(links, reporting.DeclaredLinks);
    }

    [Fact]
    public void Reports_declared_affordances_with_method_input_and_default_flag()
    {
        var config = new LinkConfigRegistry().Add(new OrderLinks()).GetConfig(typeof(Order));

        var reporting = Assert.IsAssignableFrom<IDeclarationReportingConfig>(config);
        var affordances = reporting.DeclaredAffordances;

        Assert.Equal(2, affordances.Count);

        Assert.Equal("cancel", affordances[0].Name.Value);
        Assert.Equal("POST", affordances[0].HttpMethod);
        Assert.Equal("Cancel this order", affordances[0].Title);
        Assert.Equal(typeof(CancelInput), affordances[0].InputType);
        Assert.Equal("application/json", affordances[0].ContentType);
        Assert.True(affordances[0].IsDefault);
        Assert.True(affordances[0].Conditional);

        Assert.Equal("archive", affordances[1].Name.Value);
        Assert.Equal("DELETE", affordances[1].HttpMethod);
        Assert.Null(affordances[1].InputType);
        Assert.False(affordances[1].IsDefault);
        Assert.False(affordances[1].Conditional);

        Assert.Same(affordances, reporting.DeclaredAffordances);
    }

    [Fact]
    public void A_link_gated_by_a_policy_is_reported_as_conditional()
    {
        var config = new LinkConfigRegistry().Add(new GatedLinks()).GetConfig(typeof(Order));

        var reporting = Assert.IsAssignableFrom<IDeclarationReportingConfig>(config);

        Assert.True(Assert.Single(reporting.DeclaredLinks).Conditional);
    }

    [Fact]
    public void A_config_with_no_declarations_reports_empty_lists()
    {
        var config = new LinkConfigRegistry().Add(new EmbedOnlyLinks()).GetConfig(typeof(Order));

        var reporting = Assert.IsAssignableFrom<IDeclarationReportingConfig>(config);

        Assert.Empty(reporting.DeclaredLinks);
        Assert.Empty(reporting.DeclaredAffordances);
        Assert.Same(reporting.DeclaredLinks, reporting.DeclaredLinks);
        Assert.Same(reporting.DeclaredAffordances, reporting.DeclaredAffordances);
    }

    private sealed record Order(int Id, string Status, IReadOnlyList<Order> Related);

    private sealed record CancelInput(string Reason);

    private sealed class OrderLinks : LinkConfig<Order>
    {
        public override void Configure(ILinkBuilder<Order> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
            builder.Link("customer", o => LinkTarget.Uri($"/customers/{o.Id}"))
                .Title("The customer")
                .Type("application/json")
                .Name("primary")
                .Deprecated("https://docs.example.com/gone")
                .Hreflang("en-US")
                .Profile("https://schemas.example.com/customer");
            builder.Links("item", o => o.Related.Select(r => LinkTarget.Uri($"/orders/{r.Id}")));
            builder.Link("receipt", o => LinkTarget.Uri($"/orders/{o.Id}/receipt")).When(o => o.Status == "Paid");

            builder.Affordance("cancel", o => LinkTarget.Uri($"/orders/{o.Id}/cancel"))
                .Post()
                .Title("Cancel this order")
                .Accepts<CancelInput>()
                .ContentType("application/json")
                .AsDefault()
                .When(o => o.Status == "Pending");
            builder.Affordance("archive", o => LinkTarget.Uri($"/orders/{o.Id}")).Delete();
        }
    }

    private sealed class GatedLinks : LinkConfig<Order>
    {
        public override void Configure(ILinkBuilder<Order> builder)
            => builder.Self(o => LinkTarget.Uri($"/orders/{o.Id}")).RequireAuthorization("CanSee");
    }

    private sealed class EmbedOnlyLinks : LinkConfig<Order>
    {
        public override void Configure(ILinkBuilder<Order> builder)
            => builder.EmbedMany("related", o => o.Related);
    }
}
