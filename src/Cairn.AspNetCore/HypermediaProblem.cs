using System.Text.Json;
using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore;

/// <summary>
/// An RFC 9457 problem document that can carry hypermedia. Returned from an endpoint, it writes
/// <c>application/problem+json</c> with the standard members plus any declared <c>_links</c> and <c>_actions</c>,
/// so an error response can still tell the client what to do next. When the host registered
/// <c>AddProblemDetails()</c>, the document is written through <see cref="IProblemDetailsService"/> so
/// <c>CustomizeProblemDetails</c> and custom writers apply.
/// </summary>
public sealed class HypermediaProblem : IResult
{
    private readonly List<(string Relation, LinkTarget Target, string? Title)> _links = [];
    private readonly List<(string Name, LinkTarget Target, string Method)> _actions = [];
    private readonly Dictionary<string, object?> _extensions = new(StringComparer.Ordinal);

    /// <summary>Creates a problem document with the given HTTP status code.</summary>
    public HypermediaProblem(int status) => Status = status;

    /// <summary>The HTTP status code, also written as the <c>status</c> member.</summary>
    public int Status { get; }

    /// <summary>A URI identifying the problem type (RFC 9457 <c>type</c>).</summary>
    public string? Type { get; set; }

    /// <summary>A short, human-readable summary of the problem (RFC 9457 <c>title</c>).</summary>
    public string? Title { get; set; }

    /// <summary>A human-readable explanation specific to this occurrence (RFC 9457 <c>detail</c>).</summary>
    public string? Detail { get; set; }

    /// <summary>A URI identifying the specific occurrence (RFC 9457 <c>instance</c>).</summary>
    public string? Instance { get; set; }

    /// <summary>Adds a link to the problem's <c>_links</c>.</summary>
    public HypermediaProblem WithLink(string relation, string href, string? title = null)
        => WithLink(relation, LinkTarget.Uri(href), title);

    /// <summary>
    /// Adds a link to the problem's <c>_links</c> from a <see cref="LinkTarget"/> — a named route
    /// (<see cref="LinkTarget.Route"/>), a route template, or an explicit URI — resolved by the host's URL
    /// policy (<c>UrlStyle</c>, <c>PublicBaseUri</c>) like every other Cairn link.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    public HypermediaProblem WithLink(string relation, LinkTarget target, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _links.Add((relation, target, title));
        return this;
    }

    /// <summary>Adds an affordance to the problem's <c>_actions</c> (e.g. a <c>retry</c> the client can invoke).</summary>
    public HypermediaProblem WithAction(string name, string href, string method = "POST")
        => WithAction(name, LinkTarget.Uri(href), method);

    /// <summary>
    /// Adds an affordance to the problem's <c>_actions</c> from a <see cref="LinkTarget"/>, resolved by the
    /// host's URL policy like every other Cairn link.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    public HypermediaProblem WithAction(string name, LinkTarget target, string method = "POST")
    {
        ArgumentNullException.ThrowIfNull(target);
        _actions.Add((name, target, method));
        return this;
    }

    // The RFC 9457 members are written from their dedicated properties, and the hypermedia sections from
    // WithLink/WithAction; an extension under any of these names would silently clobber them on the wire
    // (e.g. WithExtension("status", "draft") replacing the numeric status member).
    private static readonly string[] ReservedMembers = ["type", "title", "status", "detail", "instance", "_links", "_actions"];

    /// <summary>Adds a problem extension member.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty, or a reserved member name (<c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c>, <c>instance</c>, <c>_links</c>, <c>_actions</c>) — set those via the dedicated properties and methods instead.</exception>
    public HypermediaProblem WithExtension(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Array.IndexOf(ReservedMembers, name) >= 0)
        {
            throw new ArgumentException(
                $"'{name}' is a reserved problem member and cannot be set as an extension; use the dedicated property or method ({nameof(Status)}, {nameof(Title)}, {nameof(WithLink)}, ...) instead.",
                nameof(name));
        }

        _extensions[name] = value;
        return this;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = Status;

        // When the host opted into the problem-details pipeline (AddProblemDetails), write through it so
        // CustomizeProblemDetails and registered writers see this document like any framework-produced
        // problem. The hypermedia sections ride along as extensions. A writer that declines the request
        // (e.g. the default writer respects an Accept header without a JSON type) falls through to the
        // manual emission below, which preserves this type's historical wire shape.
        if (httpContext.RequestServices.GetService<IProblemDetailsService>() is { } problemDetails
            && await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = ToProblemDetails(httpContext),
            }))
        {
            return;
        }

        // Keys serialize verbatim: RFC 9457 members, extension names, and rels must never be renamed by the
        // host's DictionaryKeyPolicy.
        var body = new VerbatimKeyDictionary<object?>();
        if (Type is not null)
        {
            body["type"] = Type;
        }

        if (Title is not null)
        {
            body["title"] = Title;
        }

        body["status"] = Status;
        if (Detail is not null)
        {
            body["detail"] = Detail;
        }

        if (Instance is not null)
        {
            body["instance"] = Instance;
        }

        foreach (var (key, value) in _extensions)
        {
            body[key] = value;
        }

        if (BuildLinks(httpContext) is { } links)
        {
            body["_links"] = links;
        }

        if (BuildActions(httpContext) is { } actions)
        {
            body["_actions"] = actions;
        }

        // Dictionaries serialize as JSON objects without triggering the Cairn modifier (which only runs for
        // object contracts), so the problem keeps its application/problem+json shape verbatim.
        await httpContext.Response.WriteAsJsonAsync(body, (JsonSerializerOptions?)null, "application/problem+json");
    }

    // The framework-facing form of this document, for the IProblemDetailsService pipeline. The dedicated
    // members map onto ProblemDetails' own properties; declared extensions and the hypermedia sections become
    // extension members (their keys serialize verbatim through the VerbatimKeyDictionary values).
    private Microsoft.AspNetCore.Mvc.ProblemDetails ToProblemDetails(HttpContext httpContext)
    {
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = Status,
            Type = Type,
            Title = Title,
            Detail = Detail,
            Instance = Instance,
        };

        foreach (var (key, value) in _extensions)
        {
            problem.Extensions[key] = value;
        }

        if (BuildLinks(httpContext) is { } links)
        {
            problem.Extensions["_links"] = links;
        }

        if (BuildActions(httpContext) is { } actions)
        {
            problem.Extensions["_actions"] = actions;
        }

        return problem;
    }

    // Route-based targets resolve through the host's URL policy; an explicit URI is used as-is even when
    // Cairn isn't registered. An unresolvable target follows the configured resolution mode: dropped in Lax
    // (problem bodies should degrade, not fail), thrown in Strict.
    private static string? ResolveHref(HttpContext httpContext, LinkTarget target, string relation)
    {
        var href = httpContext.RequestServices.GetService<ILinkUrlResolver>() is { } resolver
            ? resolver.Resolve(target)
            : (target as ExplicitLinkTarget)?.Href;

        if (string.IsNullOrWhiteSpace(href))
        {
            if (httpContext.RequestServices.GetService<CairnOptions>() is { Mode: LinkResolutionMode.Strict })
            {
                throw new LinkResolutionException(
                    $"Could not resolve a URL for problem link '{relation}'. " +
                    "Ensure the endpoint is named (WithName / [Http*(Name=...)]) and all route values are supplied.");
            }

            return null;
        }

        return href;
    }

    // Mirrors the main formatter: one link per rel emits a HAL link object, several sharing a rel emit a HAL
    // link array in declaration order (rels compare case-insensitively per RFC 8288).
    private VerbatimKeyDictionary<object?>? BuildLinks(HttpContext httpContext)
    {
        if (_links.Count == 0)
        {
            return null;
        }

        var links = new VerbatimKeyDictionary<object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relation, target, title) in _links)
        {
            if (ResolveHref(httpContext, target, relation) is not { } href)
            {
                continue;
            }

            var link = new VerbatimKeyDictionary<object?> { ["href"] = href };
            if ((title ?? target.Title) is { } linkTitle)
            {
                link["title"] = linkTitle;
            }

            if (!links.TryGetValue(relation, out var existing))
            {
                links[relation] = link;
            }
            else if (existing is List<object?> array)
            {
                array.Add(link);
            }
            else
            {
                links[relation] = new List<object?> { existing, link };
            }
        }

        return links.Count > 0 ? links : null;
    }

    private VerbatimKeyDictionary<object?>? BuildActions(HttpContext httpContext)
    {
        if (_actions.Count == 0)
        {
            return null;
        }

        var actions = new VerbatimKeyDictionary<object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, target, method) in _actions)
        {
            if (ResolveHref(httpContext, target, name) is { } href)
            {
                actions[name] = new VerbatimKeyDictionary<object?> { ["href"] = href, ["method"] = method };
            }
        }

        return actions.Count > 0 ? actions : null;
    }
}

/// <summary>Factory for Cairn result types.</summary>
public static class CairnResults
{
    /// <summary>Creates a hypermedia-capable RFC 9457 problem document.</summary>
    public static HypermediaProblem Problem(int status, string? title = null, string? detail = null, string? type = null, string? instance = null)
        => new(status) { Title = title, Detail = detail, Type = type, Instance = instance };
}
