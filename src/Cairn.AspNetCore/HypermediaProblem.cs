using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>
/// An RFC 9457 problem document that can carry hypermedia. Returned from an endpoint, it writes
/// <c>application/problem+json</c> with the standard members plus any declared <c>_links</c> and <c>_actions</c>,
/// so an error response can still tell the client what to do next.
/// </summary>
public sealed class HypermediaProblem : IResult
{
    private readonly List<(string Relation, string Href, string? Title)> _links = [];
    private readonly List<(string Name, string Href, string Method)> _actions = [];
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
    {
        _links.Add((relation, href, title));
        return this;
    }

    /// <summary>Adds an affordance to the problem's <c>_actions</c> (e.g. a <c>retry</c> the client can invoke).</summary>
    public HypermediaProblem WithAction(string name, string href, string method = "POST")
    {
        _actions.Add((name, href, method));
        return this;
    }

    /// <summary>Adds a problem extension member.</summary>
    public HypermediaProblem WithExtension(string name, object? value)
    {
        _extensions[name] = value;
        return this;
    }

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
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

        if (_links.Count > 0)
        {
            body["_links"] = BuildLinks();
        }

        if (_actions.Count > 0)
        {
            body["_actions"] = BuildActions();
        }

        httpContext.Response.StatusCode = Status;

        // Dictionaries serialize as JSON objects without triggering the Cairn modifier (which only runs for
        // object contracts), so the problem keeps its application/problem+json shape verbatim.
        return httpContext.Response.WriteAsJsonAsync(body, (JsonSerializerOptions?)null, "application/problem+json");
    }

    private Dictionary<string, object?> BuildLinks()
    {
        var links = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (relation, href, title) in _links)
        {
            var link = new Dictionary<string, object?>(StringComparer.Ordinal) { ["href"] = href };
            if (title is not null)
            {
                link["title"] = title;
            }

            links[relation] = link;
        }

        return links;
    }

    private Dictionary<string, object?> BuildActions()
    {
        var actions = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, href, method) in _actions)
        {
            actions[name] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["href"] = href, ["method"] = method };
        }

        return actions;
    }
}

/// <summary>Factory for Cairn result types.</summary>
public static class CairnResults
{
    /// <summary>Creates a hypermedia-capable RFC 9457 problem document.</summary>
    public static HypermediaProblem Problem(int status, string? title = null, string? detail = null, string? type = null, string? instance = null)
        => new(status) { Title = title, Detail = detail, Type = type, Instance = instance };
}
