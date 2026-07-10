using Cairn;

namespace Cairn.Core.Tests;

public class AffordanceReportingTests
{
    [Fact]
    public void Reports_declared_affordances_with_their_metadata_in_declaration_order()
    {
        var config = new LinkConfigRegistry().Add(new OrderLinks()).GetConfig(typeof(Order));

        var reporting = Assert.IsAssignableFrom<IAffordanceReportingConfig>(config);
        var affordances = reporting.Affordances;

        Assert.Equal(4, affordances.Count);

        var cancel = affordances[0];
        Assert.Equal("cancel", cancel.Name.Value);
        Assert.Equal("POST", cancel.Method);
        Assert.Equal(typeof(CancelRequest), cancel.Input);
        Assert.Equal("Cancel the order", cancel.Title);
        Assert.Null(cancel.ContentType);
        Assert.False(cancel.IsDefault);
        Assert.Null(cancel.Policy);
        Assert.False(cancel.PolicyIsResourceBased);
        Assert.True(cancel.HasCondition);

        var approve = affordances[1];
        Assert.Equal("approve", approve.Name.Value);
        Assert.Equal("PUT", approve.Method);
        Assert.Null(approve.Input);
        Assert.Null(approve.Title);
        Assert.Equal("manager", approve.Policy);
        Assert.False(approve.PolicyIsResourceBased);
        Assert.False(approve.HasCondition);
        Assert.True(approve.IsDefault);

        var audit = affordances[2];
        Assert.Equal("audit", audit.Name.Value);
        Assert.Equal("GET", audit.Method);
        Assert.Equal("manager", audit.Policy);
        Assert.True(audit.PolicyIsResourceBased);

        var import = affordances[3];
        Assert.Equal("import", import.Name.Value);
        Assert.Equal("multipart/form-data", import.ContentType);
        Assert.Equal(string.Empty, import.Policy);   // RequireAuthorization() → the default-policy sentinel
        Assert.False(import.PolicyIsResourceBased);

        // The projection is memoized: a second read returns the same instance.
        Assert.Same(affordances, reporting.Affordances);
    }

    [Fact]
    public void A_config_with_no_affordances_reports_an_empty_list()
    {
        var config = new LinkConfigRegistry().Add(new PlainLinks()).GetConfig(typeof(Plain));

        var reporting = Assert.IsAssignableFrom<IAffordanceReportingConfig>(config);

        Assert.Empty(reporting.Affordances);
        Assert.Same(reporting.Affordances, reporting.Affordances);
    }

    [Fact]
    public void A_default_relation_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new AffordanceSchema(default, "POST"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_method_is_rejected(string? method)
    {
        Assert.Throws<ArgumentException>(() => new AffordanceSchema("cancel", method!));
    }

    private sealed record Order(int Id, string Status);

    private sealed record CancelRequest(string? Reason);

    private sealed record Plain(int Id);

    private sealed class OrderLinks : LinkConfig<Order>
    {
        public override void Configure(ILinkBuilder<Order> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
            builder.Affordance("cancel", o => LinkTarget.Uri($"/orders/{o.Id}/cancel"))
                .Post()
                .Accepts<CancelRequest>()
                .Title("Cancel the order")
                .When(o => o.Status == "Pending");
            builder.Affordance("approve", o => LinkTarget.Uri($"/orders/{o.Id}/approve"))
                .Put()
                .AsDefault()
                .RequireAuthorization("manager");
            builder.Affordance("audit", o => LinkTarget.Uri($"/orders/{o.Id}/audit"))
                .Get()
                .RequireAuthorization("manager", o => o);
            builder.Affordance("import", o => LinkTarget.Uri($"/orders/{o.Id}/import"))
                .ContentType("multipart/form-data")
                .RequireAuthorization();
        }
    }

    private sealed class PlainLinks : LinkConfig<Plain>
    {
        public override void Configure(ILinkBuilder<Plain> builder)
            => builder.Self(p => LinkTarget.Uri($"/plain/{p.Id}"));
    }
}
