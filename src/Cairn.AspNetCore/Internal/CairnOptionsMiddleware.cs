using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Primitives;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Answers <c>OPTIONS</c> with the union of methods declared by endpoints whose route pattern matches the
/// requested path (route constraints are not evaluated — the answer describes the path's shape).
/// </summary>
internal sealed class CairnOptionsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EndpointDataSource _endpoints;
    private volatile IReadOnlyList<(TemplateMatcher Matcher, IReadOnlyList<string> Methods)>? _routes;

    public CairnOptionsMiddleware(RequestDelegate next, EndpointDataSource endpoints)
    {
        _next = next;
        _endpoints = endpoints;
        ChangeToken.OnChange(endpoints.GetChangeToken, () => _routes = null);
    }

    public Task Invoke(HttpContext context)
    {
        // A CORS preflight (OPTIONS carrying Access-Control-Request-Method) belongs to the CORS middleware:
        // answering it here would return 204 without any Access-Control-* headers and the browser would
        // block the actual request.
        if (!HttpMethods.IsOptions(context.Request.Method)
            || context.Request.Headers.ContainsKey(Microsoft.Net.Http.Headers.HeaderNames.AccessControlRequestMethod)
            || AppHandlesOptions(context))
        {
            return _next(context);
        }

        var methods = MatchMethods(context.Request.Path);
        if (methods.Count == 0)
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers.Allow = string.Join(", ", methods);
        return Task.CompletedTask;
    }

    // The app mapped OPTIONS for this path itself (or an any-method endpoint matched) — its answer wins.
    private static bool AppHandlesOptions(HttpContext context)
        => context.GetEndpoint() is RouteEndpoint matched
            && (matched.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } metadata
                || metadata.HttpMethods.Count == 0
                || metadata.HttpMethods.Contains(HttpMethods.Options, StringComparer.OrdinalIgnoreCase));

    private IReadOnlyList<string> MatchMethods(PathString path)
    {
        var routes = _routes ??= BuildRoutes();

        HashSet<string>? methods = null;
        var values = new RouteValueDictionary();
        foreach (var (matcher, declared) in routes)
        {
            if (matcher.TryMatch(path, values))
            {
                methods ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var method in declared)
                {
                    methods.Add(method);
                }
            }
        }

        if (methods is null)
        {
            return [];
        }

        // HEAD is implied by GET, and OPTIONS is what we're answering; a stable conventional order reads best.
        if (methods.Contains(HttpMethods.Get))
        {
            methods.Add(HttpMethods.Head);
        }

        methods.Add(HttpMethods.Options);

        var ordered = new List<string>(methods.Count);
        foreach (var known in (string[])[HttpMethods.Get, HttpMethods.Head, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Options])
        {
            if (methods.Remove(known))
            {
                ordered.Add(known);
            }
        }

        ordered.AddRange(methods.Order(StringComparer.Ordinal));
        return ordered;
    }

    private List<(TemplateMatcher, IReadOnlyList<string>)> BuildRoutes()
    {
        var routes = new List<(TemplateMatcher, IReadOnlyList<string>)>();
        foreach (var endpoint in _endpoints.Endpoints)
        {
            // Only endpoints that declare their methods contribute; an any-method endpoint says nothing useful.
            if (endpoint is not RouteEndpoint { RoutePattern.RawText: { } template } route
                || route.Metadata.GetMetadata<IHttpMethodMetadata>() is not { HttpMethods.Count: > 0 } metadata)
            {
                continue;
            }

            routes.Add((
                new TemplateMatcher(TemplateParser.Parse(template), new RouteValueDictionary(route.RoutePattern.Defaults)),
                metadata.HttpMethods));
        }

        return routes;
    }
}
