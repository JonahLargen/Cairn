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

    /// <summary>Adds an affordance (available action) with the given name.</summary>
    IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkTarget> target);

    /// <summary>Adds an affordance with the given name, computing its target with access to the request's services.</summary>
    IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkContext, ValueTask<LinkTarget>> target);
}

/// <summary>Configures a single link.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public interface ILinkSpec<T>
{
    /// <summary>Sets a human-readable title.</summary>
    ILinkSpec<T> Title(string title);

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

    /// <summary>Sets a human-readable title.</summary>
    IAffordanceSpec<T> Title(string title);

    /// <summary>Includes the affordance only when the predicate holds for the resource.</summary>
    IAffordanceSpec<T> When(Func<T, bool> condition);

    /// <summary>Includes the affordance only when the async predicate holds, with access to the request's services.</summary>
    IAffordanceSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition);

    /// <summary>Includes the affordance only when the caller satisfies the named authorization policy.</summary>
    IAffordanceSpec<T> RequireAuthorization(string policy);
}
