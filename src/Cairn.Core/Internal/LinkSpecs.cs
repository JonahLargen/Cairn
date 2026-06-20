namespace Cairn.Internal;

/// <summary>Common, mutable state shared by link and affordance specifications.</summary>
internal abstract class HypermediaSpec<T>
{
    public required LinkRelation Relation { get; init; }

    public required Func<T, LinkTarget> Target { get; init; }

    public Func<T, bool>? Condition { get; set; }

    public string? Policy { get; set; }

    public string? TitleText { get; set; }
}

/// <summary>A recorded link declaration.</summary>
internal sealed class LinkSpec<T> : HypermediaSpec<T>, ILinkSpec<T>
{
    public ILinkSpec<T> Title(string title)
    {
        TitleText = title;
        return this;
    }

    public ILinkSpec<T> When(Func<T, bool> condition)
    {
        Condition = condition;
        return this;
    }

    public ILinkSpec<T> RequireAuthorization(string policy)
    {
        Policy = policy;
        return this;
    }
}

/// <summary>A recorded affordance declaration.</summary>
internal sealed class AffordanceSpec<T> : HypermediaSpec<T>, IAffordanceSpec<T>
{
    public string HttpMethod { get; private set; } = "POST";

    public Type? InputType { get; private set; }

    public IAffordanceSpec<T> Method(string httpMethod)
    {
        HttpMethod = httpMethod;
        return this;
    }

    public IAffordanceSpec<T> Accepts<TInput>()
    {
        InputType = typeof(TInput);
        return this;
    }

    public IAffordanceSpec<T> Title(string title)
    {
        TitleText = title;
        return this;
    }

    public IAffordanceSpec<T> When(Func<T, bool> condition)
    {
        Condition = condition;
        return this;
    }

    public IAffordanceSpec<T> RequireAuthorization(string policy)
    {
        Policy = policy;
        return this;
    }
}

/// <summary>Records link and affordance declarations from a <see cref="LinkConfig{T}"/>.</summary>
internal sealed class LinkBuilder<T> : ILinkBuilder<T>
{
    public List<LinkSpec<T>> Links { get; } = [];

    public List<AffordanceSpec<T>> Affordances { get; } = [];

    public ILinkSpec<T> Self(Func<T, LinkTarget> target) => Link(IanaLinkRelations.Self, target);

    public ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkTarget> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var spec = new LinkSpec<T> { Relation = relation, Target = target };
        Links.Add(spec);
        return spec;
    }

    public IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkTarget> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var spec = new AffordanceSpec<T> { Relation = name, Target = target };
        Affordances.Add(spec);
        return spec;
    }
}
