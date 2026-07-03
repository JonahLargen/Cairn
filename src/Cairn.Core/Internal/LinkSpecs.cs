namespace Cairn.Internal;

/// <summary>Common, mutable state shared by link and affordance specifications.</summary>
internal abstract class HypermediaSpec<T>
{
    /// <summary>The policy sentinel meaning "the host's default authorization policy".</summary>
    public const string DefaultPolicy = "";

    public required LinkRelation Relation { get; init; }

    public Func<T, LinkContext, ValueTask<bool>>? Condition { get; set; }

    public string? Policy { get; set; }

    public string? TitleText { get; set; }

    public string? TypeText { get; set; }
}

/// <summary>A recorded link declaration; yields one or more targets for its relation.</summary>
internal sealed class LinkSpec<T> : HypermediaSpec<T>, ILinkSpec<T>
{
    // Exactly one of these is set. Link()/Self() declare a single target; keeping that delegate unwrapped
    // lets the build stage resolve it without allocating a one-element array and enumerator per resource.
    public Func<T, LinkContext, ValueTask<LinkTarget>>? SingleTarget { get; init; }

    public Func<T, LinkContext, ValueTask<IEnumerable<LinkTarget>>>? Targets { get; init; }

    public string? NameText { get; private set; }

    public string? DeprecationText { get; private set; }

    public string? HreflangText { get; private set; }

    public string? ProfileText { get; private set; }

    public ILinkSpec<T> Title(string title)
    {
        TitleText = title;
        return this;
    }

    public ILinkSpec<T> Type(string mediaType)
    {
        TypeText = mediaType;
        return this;
    }

    public ILinkSpec<T> Name(string name)
    {
        NameText = name;
        return this;
    }

    public ILinkSpec<T> Deprecated(string url)
    {
        DeprecationText = url;
        return this;
    }

    public ILinkSpec<T> Hreflang(string language)
    {
        HreflangText = language;
        return this;
    }

    public ILinkSpec<T> Profile(string profileUri)
    {
        ProfileText = profileUri;
        return this;
    }

    public ILinkSpec<T> When(Func<T, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return When((resource, _) => new ValueTask<bool>(condition(resource)));
    }

    public ILinkSpec<T> When(Func<T, LinkContext, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return When((resource, context) => new ValueTask<bool>(condition(resource, context)));
    }

    public ILinkSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        Condition = condition;
        return this;
    }

    public ILinkSpec<T> RequireAuthorization(string policy)
    {
        Policy = policy;
        return this;
    }

    public ILinkSpec<T> RequireAuthorization() => RequireAuthorization(HypermediaSpec<T>.DefaultPolicy);
}

/// <summary>A recorded affordance declaration.</summary>
internal sealed class AffordanceSpec<T> : HypermediaSpec<T>, IAffordanceSpec<T>
{
    public required Func<T, LinkContext, ValueTask<LinkTarget>> Target { get; init; }

    public string HttpMethod { get; private set; } = "POST";

    public Type? InputType { get; private set; }

    public string? ContentTypeText { get; private set; }

    public bool IsDefault { get; private set; }

    public IAffordanceSpec<T> Method(string httpMethod)
    {
        HttpMethod = httpMethod;
        return this;
    }

    public IAffordanceSpec<T> Get() => Method("GET");

    public IAffordanceSpec<T> Post() => Method("POST");

    public IAffordanceSpec<T> Put() => Method("PUT");

    public IAffordanceSpec<T> Patch() => Method("PATCH");

    public IAffordanceSpec<T> Delete() => Method("DELETE");

    public IAffordanceSpec<T> Accepts<TInput>()
    {
        InputType = typeof(TInput);
        return this;
    }

    public IAffordanceSpec<T> ContentType(string contentType)
    {
        ContentTypeText = contentType;
        return this;
    }

    public IAffordanceSpec<T> AsDefault()
    {
        IsDefault = true;
        return this;
    }

    public IAffordanceSpec<T> Title(string title)
    {
        TitleText = title;
        return this;
    }

    public IAffordanceSpec<T> When(Func<T, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return When((resource, _) => new ValueTask<bool>(condition(resource)));
    }

    public IAffordanceSpec<T> When(Func<T, LinkContext, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return When((resource, context) => new ValueTask<bool>(condition(resource, context)));
    }

    public IAffordanceSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        Condition = condition;
        return this;
    }

    public IAffordanceSpec<T> RequireAuthorization(string policy)
    {
        Policy = policy;
        return this;
    }

    public IAffordanceSpec<T> RequireAuthorization() => RequireAuthorization(HypermediaSpec<T>.DefaultPolicy);
}

/// <summary>A recorded embedded-resource declaration; resolves the child instance(s) to embed.</summary>
internal sealed class EmbedSpec<T>
{
    public required LinkRelation Relation { get; init; }

    public required bool Single { get; init; }

    public required Func<T, IReadOnlyList<object>> Resolve { get; init; }
}

/// <summary>Records link, affordance, and embed declarations from a <see cref="LinkConfig{T}"/>.</summary>
internal sealed class LinkBuilder<T> : ILinkBuilder<T>
{
    public List<LinkSpec<T>> LinkSpecs { get; } = [];

    public List<AffordanceSpec<T>> AffordanceSpecs { get; } = [];

    public List<EmbedSpec<T>> EmbedSpecs { get; } = [];

    public ILinkSpec<T> Self(Func<T, LinkTarget> target) => Link(IanaLinkRelations.Self, target);

    public ILinkSpec<T> Self(Func<T, LinkContext, LinkTarget> target) => Link(IanaLinkRelations.Self, target);

    public ILinkSpec<T> Self(Func<T, LinkContext, ValueTask<LinkTarget>> target) => Link(IanaLinkRelations.Self, target);

    public ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkTarget> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Link(relation, (resource, _) => new ValueTask<LinkTarget>(target(resource)));
    }

    public ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkContext, LinkTarget> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Link(relation, (resource, context) => new ValueTask<LinkTarget>(target(resource, context)));
    }

    public ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkContext, ValueTask<LinkTarget>> target)
    {
        // Fail at configuration time: a default(LinkRelation) that slipped past the compiler's nullability
        // analysis would otherwise surface mid-serialization.
        relation.ThrowIfDefault(nameof(relation));
        ArgumentNullException.ThrowIfNull(target);
        var spec = new LinkSpec<T> { Relation = relation, SingleTarget = target };
        LinkSpecs.Add(spec);
        return spec;
    }

    public ILinkSpec<T> Links(LinkRelation relation, Func<T, IEnumerable<LinkTarget>> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return Links(relation, (resource, _) => new ValueTask<IEnumerable<LinkTarget>>(targets(resource)));
    }

    public ILinkSpec<T> Links(LinkRelation relation, Func<T, LinkContext, ValueTask<IEnumerable<LinkTarget>>> targets)
    {
        relation.ThrowIfDefault(nameof(relation));
        ArgumentNullException.ThrowIfNull(targets);
        var spec = new LinkSpec<T> { Relation = relation, Targets = targets };
        LinkSpecs.Add(spec);
        return spec;
    }

    public IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkTarget> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Affordance(name, (resource, _) => new ValueTask<LinkTarget>(target(resource)));
    }

    public IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkContext, LinkTarget> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Affordance(name, (resource, context) => new ValueTask<LinkTarget>(target(resource, context)));
    }

    public IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkContext, ValueTask<LinkTarget>> target)
    {
        name.ThrowIfDefault(nameof(name));
        ArgumentNullException.ThrowIfNull(target);
        var spec = new AffordanceSpec<T> { Relation = name, Target = target };
        AffordanceSpecs.Add(spec);
        return spec;
    }

    public void Embed<TChild>(LinkRelation relation, Func<T, TChild?> resource) where TChild : class
    {
        relation.ThrowIfDefault(nameof(relation));
        ArgumentNullException.ThrowIfNull(resource);
        EmbedSpecs.Add(new EmbedSpec<T> { Relation = relation, Single = true, Resolve = t => resource(t) is { } child ? new object[] { child } : [] });
    }

    public void EmbedMany<TChild>(LinkRelation relation, Func<T, IEnumerable<TChild>?> resources)
    {
        relation.ThrowIfDefault(nameof(relation));
        ArgumentNullException.ThrowIfNull(resources);
        EmbedSpecs.Add(new EmbedSpec<T>
        {
            Relation = relation,
            Single = false,
            Resolve = t =>
            {
                if (resources(t) is not { } items)
                {
                    return [];
                }

                var list = new List<object>();
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        list.Add(item);
                    }
                }

                return list;
            },
        });
    }
}
