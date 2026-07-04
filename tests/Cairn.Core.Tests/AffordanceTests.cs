namespace Cairn.Core.Tests;

public class AffordanceTests
{
    [Fact]
    public void Constructing_an_affordance_sets_name_href_and_method()
    {
        var affordance = new Affordance("cancel", "/orders/1/cancel", "POST");

        Assert.Equal("cancel", affordance.Name.Value);
        Assert.Equal("/orders/1/cancel", affordance.Href);
        Assert.Equal("POST", affordance.Method);
    }

    [Fact]
    public void Affordance_requires_a_non_empty_href()
    {
        Assert.Throws<ArgumentException>(() => new Affordance("cancel", "   ", "POST"));
    }

    [Fact]
    public void Affordance_requires_a_non_empty_method()
    {
        Assert.Throws<ArgumentException>(() => new Affordance("cancel", "/orders/1/cancel", "   "));
    }
}
