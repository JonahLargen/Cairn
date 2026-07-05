using System.Diagnostics.CodeAnalysis;

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
    ILinkSpec<T> Self(Func<T, LinkContext, LinkTarget> target);

    /// <summary>Adds the <c>self</c> link, computing its target asynchronously with access to the request's services.</summary>
    ILinkSpec<T> Self(Func<T, LinkContext, ValueTask<LinkTarget>> target);

    /// <summary>Adds a link with the given relation.</summary>
    ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkTarget> target);

    /// <summary>Adds a link with the given relation, computing its target with access to the request's services.</summary>
    ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkContext, LinkTarget> target);

    /// <summary>Adds a link with the given relation, computing its target asynchronously with access to the request's services.</summary>
    ILinkSpec<T> Link(LinkRelation relation, Func<T, LinkContext, ValueTask<LinkTarget>> target);

    /// <summary>Adds multiple links sharing one relation, emitted as a HAL link array (e.g. one <c>item</c> per child).</summary>
    ILinkSpec<T> Links(LinkRelation relation, Func<T, IEnumerable<LinkTarget>> targets);

    /// <summary>Adds multiple links sharing one relation, computing their targets with access to the request's services.</summary>
    ILinkSpec<T> Links(LinkRelation relation, Func<T, LinkContext, ValueTask<IEnumerable<LinkTarget>>> targets);

    /// <summary>Adds an affordance (available action) with the given name.</summary>
    IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkTarget> target);

    /// <summary>Adds an affordance with the given name, computing its target with access to the request's services.</summary>
    IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkContext, LinkTarget> target);

    /// <summary>Adds an affordance with the given name, computing its target asynchronously with access to the request's services.</summary>
    IAffordanceSpec<T> Affordance(LinkRelation name, Func<T, LinkContext, ValueTask<LinkTarget>> target);

    /// <summary>
    /// Embeds a related resource under the given relation in HAL <c>_embedded</c>, decorated with its own links.
    /// A null result embeds nothing. Note that a child which is also a serialized property of the resource
    /// appears twice — in the body and in <c>_embedded</c> — unless the property is marked
    /// <c>[JsonIgnore]</c> (or the DTO doesn't expose it).
    /// </summary>
    /// <typeparam name="TChild">The embedded resource type.</typeparam>
    /// <returns>A spec for gating the embed with <c>When</c>/<c>RequireAuthorization</c>.</returns>
    IEmbedSpec<T> Embed<TChild>(LinkRelation relation, Func<T, TChild?> resource) where TChild : class;

    /// <summary>
    /// Embeds a collection of related resources under the given relation in HAL <c>_embedded</c> (always an
    /// array), each decorated with its own links. Note that children also exposed as a serialized property of
    /// the resource appear twice unless the property is marked <c>[JsonIgnore]</c>.
    /// </summary>
    /// <typeparam name="TChild">The embedded item type.</typeparam>
    /// <returns>A spec for gating the embed with <c>When</c>/<c>RequireAuthorization</c>.</returns>
    IEmbedSpec<T> EmbedMany<TChild>(LinkRelation relation, Func<T, IEnumerable<TChild>?> resources);
}

/// <summary>Configures a single link.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public interface ILinkSpec<T>
{
    /// <summary>Sets a human-readable title.</summary>
    ILinkSpec<T> Title(string title);

    /// <summary>Sets a media type hint for the link's destination (the RFC 8288 <c>type</c> attribute).</summary>
    ILinkSpec<T> Type(string mediaType);

    /// <summary>Sets a secondary key for selecting between links that share a relation (HAL/RFC 8288 <c>name</c>).</summary>
    ILinkSpec<T> Name(string name);

    /// <summary>Marks the link deprecated; <paramref name="url"/> should point at information about the deprecation.</summary>
    ILinkSpec<T> Deprecated(string url);

    /// <summary>Sets a language hint for the link's destination (RFC 8288 <c>hreflang</c>).</summary>
    ILinkSpec<T> Hreflang(string language);

    /// <summary>Sets a profile URI describing the link's destination (RFC 6906 <c>profile</c>).</summary>
    ILinkSpec<T> Profile(string profileUri);

    /// <summary>Includes the link only when the predicate holds for the resource.</summary>
    ILinkSpec<T> When(Func<T, bool> condition);

    /// <summary>Includes the link only when the predicate holds, with access to the request's services.</summary>
    ILinkSpec<T> When(Func<T, LinkContext, bool> condition);

    /// <summary>Includes the link only when the async predicate holds, with access to the request's services.</summary>
    ILinkSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition);

    /// <summary>
    /// Includes the link only when the caller satisfies the named authorization policy. The policy is evaluated
    /// against the caller alone (memoized per request) — it cannot see the resource. For a per-resource decision,
    /// use <see cref="RequireAuthorization(string, Func{T, object})"/>.
    /// </summary>
    ILinkSpec<T> RequireAuthorization(string policy);

    /// <summary>
    /// Includes the link only when the caller satisfies the named policy evaluated against the object returned by
    /// <paramref name="resource"/> — ASP.NET Core resource-based authorization
    /// (<c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c>). Pass <c>o =&gt; o</c> to authorize
    /// against the resource being linked, or select a projection or domain entity the policy's handlers expect.
    /// The policy name is validated at startup exactly as the caller-only overload's is.
    /// </summary>
    /// <param name="policy">The policy name, or the empty string for the host's default policy.</param>
    /// <param name="resource">Selects the object the policy is evaluated against.</param>
    /// <remarks>
    /// A default method (rather than an abstract one, which would break existing implementers) expressed over
    /// <see cref="When(Func{T, LinkContext, ValueTask{bool}})"/> and the authorizer's resource seam. The built-in
    /// spec overrides it so the policy name is also visible to startup validation.
    /// </remarks>
    [ExcludeFromCodeCoverage(Justification = "Non-breaking default for external ILinkSpec<T> implementers; every built-in spec overrides it, so this body is unreachable from the library's own types.")]
    ILinkSpec<T> RequireAuthorization(string policy, Func<T, object?> resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return When((item, context) => context.Authorizer.AuthorizeAsync(resource(item), policy, context.CancellationToken));
    }

    /// <summary>Includes the link only when the caller satisfies the default authorization policy (an authenticated user, by default).</summary>
    ILinkSpec<T> RequireAuthorization();
}

/// <summary>Configures a single affordance.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public interface IAffordanceSpec<T>
{
    /// <summary>Sets the HTTP method used to invoke the action (default <c>POST</c>).</summary>
    IAffordanceSpec<T> Method(string httpMethod);

    /// <summary>Sets the method to <c>GET</c>.</summary>
    IAffordanceSpec<T> Get();

    /// <summary>Sets the method to <c>POST</c>.</summary>
    IAffordanceSpec<T> Post();

    /// <summary>Sets the method to <c>PUT</c>.</summary>
    IAffordanceSpec<T> Put();

    /// <summary>Sets the method to <c>PATCH</c>.</summary>
    IAffordanceSpec<T> Patch();

    /// <summary>Sets the method to <c>DELETE</c>.</summary>
    IAffordanceSpec<T> Delete();

    /// <summary>Declares the input type the action accepts, used to describe its form fields (e.g. HAL-FORMS).</summary>
    /// <typeparam name="TInput">The request/body type the action accepts. Its public properties are preserved under trimming so form fields can be derived from them.</typeparam>
    IAffordanceSpec<T> Accepts<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TInput>();

    /// <summary>Sets the content type the action's input is submitted as (HAL-FORMS <c>contentType</c>, e.g. <c>multipart/form-data</c>).</summary>
    IAffordanceSpec<T> ContentType(string contentType);

    /// <summary>
    /// Marks this affordance as the resource's primary action. In HAL-FORMS it is emitted under the reserved
    /// <c>default</c> template key (the key HAL-FORMS clients look up first); other formats keep the
    /// affordance's declared name.
    /// </summary>
    IAffordanceSpec<T> AsDefault();

    /// <summary>Sets a human-readable title.</summary>
    IAffordanceSpec<T> Title(string title);

    /// <summary>Includes the affordance only when the predicate holds for the resource.</summary>
    IAffordanceSpec<T> When(Func<T, bool> condition);

    /// <summary>Includes the affordance only when the predicate holds, with access to the request's services.</summary>
    IAffordanceSpec<T> When(Func<T, LinkContext, bool> condition);

    /// <summary>Includes the affordance only when the async predicate holds, with access to the request's services.</summary>
    IAffordanceSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition);

    /// <summary>
    /// Includes the affordance only when the caller satisfies the named authorization policy. The policy is
    /// evaluated against the caller alone (memoized per request) — it cannot see the resource. For a per-resource
    /// decision, use <see cref="RequireAuthorization(string, Func{T, object})"/>.
    /// </summary>
    IAffordanceSpec<T> RequireAuthorization(string policy);

    /// <summary>
    /// Includes the affordance only when the caller satisfies the named policy evaluated against the object
    /// returned by <paramref name="resource"/> — ASP.NET Core resource-based authorization
    /// (<c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c>). Pass <c>o =&gt; o</c> to authorize
    /// against the resource the action belongs to, or select a projection or domain entity the policy's handlers
    /// expect. The policy name is validated at startup exactly as the caller-only overload's is.
    /// </summary>
    /// <param name="policy">The policy name, or the empty string for the host's default policy.</param>
    /// <param name="resource">Selects the object the policy is evaluated against.</param>
    /// <remarks>
    /// A default method (rather than an abstract one, which would break existing implementers) expressed over
    /// <see cref="When(Func{T, LinkContext, ValueTask{bool}})"/> and the authorizer's resource seam. The built-in
    /// spec overrides it so the policy name is also visible to startup validation.
    /// </remarks>
    [ExcludeFromCodeCoverage(Justification = "Non-breaking default for external IAffordanceSpec<T> implementers; every built-in spec overrides it, so this body is unreachable from the library's own types.")]
    IAffordanceSpec<T> RequireAuthorization(string policy, Func<T, object?> resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return When((item, context) => context.Authorizer.AuthorizeAsync(resource(item), policy, context.CancellationToken));
    }

    /// <summary>Includes the affordance only when the caller satisfies the default authorization policy (an authenticated user, by default).</summary>
    IAffordanceSpec<T> RequireAuthorization();
}

/// <summary>Configures a single embedded-resource declaration.</summary>
/// <typeparam name="T">The resource type.</typeparam>
public interface IEmbedSpec<T>
{
    /// <summary>Embeds only when the predicate holds for the resource. Skipping an embed omits its relation from <c>_embedded</c> entirely.</summary>
    IEmbedSpec<T> When(Func<T, bool> condition);

    /// <summary>Embeds only when the predicate holds, with access to the request's services.</summary>
    IEmbedSpec<T> When(Func<T, LinkContext, bool> condition);

    /// <summary>Embeds only when the async predicate holds, with access to the request's services.</summary>
    IEmbedSpec<T> When(Func<T, LinkContext, ValueTask<bool>> condition);

    /// <summary>
    /// Embeds only when the caller satisfies the named authorization policy. The policy is evaluated against the
    /// caller alone (memoized per request) — it cannot see the resource. For per-resource decisions, use
    /// <see cref="When(Func{T, LinkContext, ValueTask{bool}})"/> and call
    /// <c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c> yourself.
    /// </summary>
    IEmbedSpec<T> RequireAuthorization(string policy);

    /// <summary>Embeds only when the caller satisfies the default authorization policy (an authenticated user, by default).</summary>
    IEmbedSpec<T> RequireAuthorization();
}
