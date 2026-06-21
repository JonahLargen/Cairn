namespace Cairn;

/// <summary>Declares the links and affordances for resources of type <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public abstract class LinkConfig<T>
{
    /// <summary>Configures the links and affordances for <typeparamref name="T"/>.</summary>
    public abstract void Configure(ILinkBuilder<T> builder);
}

/// <summary>Fluent surface for declaring links and affordances on a resource type.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public interface ILinkBuilder<T>
{
    /// <summary>Adds the <c>self</c> link.</summary>
    ILinkSpec<T> Self(Func<T, LinkTarget> target);

    /// <summary>Adds the <c>self</c> link, computing its target with access to the request's services.</summary>
    ILinkSpec<T> Self(Func<T, LinkContext, ValueTask<LinkTarget>> target);

    /// <summary>Adds a link with the given relation.</summary>
    ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkTarget> target);

    /// <summary>Adds a link with the given relation, computing its target with access to the request's services.</summary>
    ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkContext, ValueTask<LinkTarget>> target);

    /// <summary>Adds multiple links sharing one relation, emitted as a HAL link array (e.g. one <c>item</c> per child).</summary>
    ILinkSpec<T> Links(LinkRelation relation, Func<T, IEnumerable<LinkTarget>> targets);

    /// <summary>Adds multiple links sharing one relation, computing their targets with access to the request's services.</summary>
    ILinkSpec<T> Links(LinkRelation relation, Func<T, LinkContext, ValueTask<IEnumerable<LinkTarget>>> targets);

    /// <summary>Adds an affordance (available action) with the given name.</summary>
    IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkTarget> target);

    /// <summary>Adds an affordance with the given name, computing its target with access to the request's services.</summary>
    IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkContext, ValueTask<LinkTarget>> target);

    /// <summary>Embeds a related resource under the given relation in HAL <c>_embedded</c>, decorated with its own links. A null result embeds nothing.</summary>
    /// <typeparam name="TChild">The embedded resource type.</typeparam>
    void Embed<TChild>(LinkRelation relation, Func<T, TChild?> resource) where TChild : class;

    /// <summary>Embeds a collection of related resources under the given relation in HAL <c>_embedded</c> (always an array), each decorated with its own links.</summary>
    /// <typeparam name="TChild">The embedded item type.</typeparam>
    void EmbedMany<TChild>(LinkRelation relation, Func<T, IEnumerable<TChild>?> resources);
}

/// <summary>Configures a single link.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public interface ILinkSpec<T>
{
    /// <summary>Sets a human-readable title.</summary>
    ILinkSpec<T> Title(string title);

    /// <summary>Sets a media type hint for the link's destination (the RFC 8288 <c>type</c> attribute).</summary>
    ILinkSpec<T> Type(string mediaType);

    /// <summary>Includes the link only when the predicate holds for the resource.</summary>
    ILinkSpec<T> When(Func<T, bool> condition);

    /// <summary>Includes the link only when the async predicate holds, with access to the request's services.</summary>
    ILinkSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition);

    /// <summary>Includes the link only when the caller satisfies the named authorization policy.</summary>
    ILinkSpec<T> RequireAuthorization(string policy);
}

/// <summary>Configures a single affordance.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public interface IAffordanceSpec<T>
{
    /// <summary>Sets the HTTP method used to invoke the action (default <c>POST</c>).</summary>
    IAffordanceSpec<T> Method(string httpMethod);

    /// <summary>Declares the input type the action accepts, used to describe its form fields (e.g. HAL-FORMS).</summary>
    /// <typeparam name="TInput">The request/body type the action accepts.</typeparam>
    IAffordanceSpec<T> Accepts<TInput>();

    /// <summary>Sets the content type the action's input is submitted as (HAL-FORMS <c>contentType</c>, e.g. <c>multipart/form-data</c>).</summary>
    IAffordanceSpec<T> ContentType(string contentType);

    /// <summary>Sets a human-readable title.</summary>
    IAffordanceSpec<T> Title(string title);

    /// <summary>Includes the affordance only when the predicate holds for the resource.</summary>
    IAffordanceSpec<T> When(Func<T, bool> condition);

    /// <summary>Includes the affordance only when the async predicate holds, with access to the request's services.</summary>
    IAffordanceSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition);

    /// <summary>Includes the affordance only when the caller satisfies the named authorization policy.</summary>
    IAffordanceSpec<T> RequireAuthorization(string policy);
}
