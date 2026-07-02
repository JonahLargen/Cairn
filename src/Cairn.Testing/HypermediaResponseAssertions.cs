using AwesomeAssertions;

namespace Cairn.Testing;

/// <summary>Entry point for fluent assertions over a <see cref="HypermediaResponse"/>.</summary>
public static class HypermediaResponseAssertionsExtensions
{
    /// <summary>Returns fluent assertions for the hypermedia response.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public static HypermediaResponseAssertions Should(this HypermediaResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new HypermediaResponseAssertions(response);
    }
}

/// <summary>Fluent assertions over the links and affordances of a <see cref="HypermediaResponse"/>.</summary>
public sealed class HypermediaResponseAssertions
{
    private readonly HypermediaResponse _subject;

    internal HypermediaResponseAssertions(HypermediaResponse subject) => _subject = subject;

    /// <summary>Continues a chain of assertions.</summary>
    public HypermediaResponseAssertions And => this;

    /// <summary>Asserts the response exposes a <c>self</c> link.</summary>
    public HypermediaResponseAssertions HaveSelfLink() => HaveLink("self");

    /// <summary>Asserts the response exposes a link with the given relation.</summary>
    public HypermediaResponseAssertions HaveLink(string relation)
    {
        _subject.Links.ContainsKey(relation).Should().BeTrue("the response should expose a '{0}' link", relation);
        return this;
    }

    /// <summary>Asserts the response exposes a link with the given relation and href (any member of a link array may match).</summary>
    public HypermediaResponseAssertions HaveLink(string relation, string href)
    {
        HaveLink(relation);
        var links = _subject.AllLinks[relation];
        if (links.Count == 1)
        {
            links[0].Href.Should().Be(href, "the '{0}' link should point to the expected href", relation);
        }
        else
        {
            links.Select(link => link.Href).Should().Contain(href, "one of the '{0}' links should point to the expected href", relation);
        }

        return this;
    }

    /// <summary>Asserts the response does not expose a link with the given relation.</summary>
    public HypermediaResponseAssertions NotHaveLink(string relation)
    {
        _subject.Links.ContainsKey(relation).Should().BeFalse("the response should not expose a '{0}' link", relation);
        return this;
    }

    /// <summary>Asserts the response exposes the named affordance, returning assertions for it.</summary>
    public HypermediaAffordanceAssertions HaveAffordance(string name)
    {
        _subject.Affordances.ContainsKey(name).Should().BeTrue("the response should expose the '{0}' affordance", name);
        return new HypermediaAffordanceAssertions(this, name, _subject.Affordances[name]);
    }

    /// <summary>Asserts the response does not expose the named affordance.</summary>
    public HypermediaResponseAssertions NotHaveAffordance(string name)
    {
        _subject.Affordances.ContainsKey(name).Should().BeFalse("the response should not expose the '{0}' affordance", name);
        return this;
    }
}

/// <summary>Fluent assertions over a single affordance.</summary>
public sealed class HypermediaAffordanceAssertions
{
    private readonly HypermediaResponseAssertions _parent;
    private readonly string _name;
    private readonly HypermediaAffordance _affordance;

    internal HypermediaAffordanceAssertions(HypermediaResponseAssertions parent, string name, HypermediaAffordance affordance)
    {
        _parent = parent;
        _name = name;
        _affordance = affordance;
    }

    /// <summary>Continues asserting on the response.</summary>
    public HypermediaResponseAssertions And => _parent;

    /// <summary>Asserts the affordance is invoked with the given HTTP method.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is null.</exception>
    public HypermediaAffordanceAssertions WithMethod(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        _affordance.Method.Should().Be(method.Method, "the '{0}' affordance should use the expected method", _name);
        return this;
    }

    /// <summary>Asserts the affordance targets the given href.</summary>
    public HypermediaAffordanceAssertions WithHref(string href)
    {
        _affordance.Href.Should().Be(href, "the '{0}' affordance should target the expected href", _name);
        return this;
    }
}
