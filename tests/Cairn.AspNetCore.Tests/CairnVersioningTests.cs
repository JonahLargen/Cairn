using System.Text.Json;
using Asp.Versioning;
using Asp.Versioning.Builder;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnVersioningTests
{
    [Fact]
    public async Task Url_segment_version_flows_into_links_automatically()
    {
        await using var app = await StartAsync(new UrlSegmentApiVersionReader(), "/v{version:apiVersion}/orders/{id:int}");

        var self = await SelfHrefAsync(app, "/v1/orders/42");

        // The version is a route value, so the current request's ambient version fills the link — no Cairn config.
        Assert.EndsWith("/v1/orders/42", self);
    }

    [Fact]
    public async Task Query_string_version_is_dropped_from_route_links_without_a_transform()
    {
        await using var app = await StartAsync(new QueryStringApiVersionReader(), "/orders/{id:int}");

        var self = await SelfHrefAsync(app, "/orders/42?api-version=1.0");

        // The version is not a route value, so GetUriByName omits it — this is the gap.
        Assert.DoesNotContain("api-version", self);
    }

    [Fact]
    public async Task Query_string_version_is_carried_into_links_with_a_transform()
    {
        await using var app = await StartAsync(new QueryStringApiVersionReader(), "/orders/{id:int}", o =>
            o.TransformUrl = (http, url) =>
                http.Request.Query.TryGetValue("api-version", out var version) && version.Count > 0
                    ? QueryHelpers.AddQueryString(url, "api-version", version.ToString())
                    : url);

        var self = await SelfHrefAsync(app, "/orders/42?api-version=1.0");

        Assert.Contains("api-version=1.0", self);
    }

    [Fact]
    public async Task An_idempotent_transform_carries_the_query_version_without_doubling_it_on_pagination_links()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.TransformUrl = (http, url) =>
            http.Request.Query.TryGetValue("api-version", out var v) && v.Count > 0
             && !url.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? QueryHelpers.AddQueryString(url, "api-version", v.ToString())
                : url);

        var app = builder.Build();
        app.MapGet("/orders", (int page = 1) => TypedResults.Ok(
            new PagedResource<VersionedOrder>([new VersionedOrder(1)], page, PageSize: 1, TotalCount: 5))).WithLinks();
        await app.StartAsync();
        await using var _ = app;

        using var client = app.GetTestClient();
        var links = JsonDocument.Parse(await client.GetStringAsync("/orders?api-version=1.0&page=2"))
            .RootElement.GetProperty("_links");

        // The default pagination links already preserve api-version; the guard keeps the transform from
        // appending a second one, so every navigation link stays on the caller's version exactly once.
        foreach (var rel in new[] { "self", "first", "next", "prev", "last" })
        {
            var href = links.GetProperty(rel).GetProperty("href").GetString()!;
            Assert.Contains("api-version=1.0", href);
            Assert.Equal(1, href.Split("api-version=").Length - 1);
        }
    }

    private static async Task<WebApplication> StartAsync(IApiVersionReader reader, string template, Action<CairnOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiVersioning(o =>
        {
            o.DefaultApiVersion = new ApiVersion(1.0);
            o.AssumeDefaultVersionWhenUnspecified = true;
            o.ApiVersionReader = reader;
        });
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new VersionedOrderLinks());
            configure?.Invoke(o);
        });

        var app = builder.Build();
        var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1.0)).Build();
        app.MapGet(template, (int id) => TypedResults.Ok(new VersionedOrder(id)))
            .WithApiVersionSet(versionSet)
            .MapToApiVersion(new ApiVersion(1.0))
            .WithName("V_GetOrder")
            .WithLinks();

        await app.StartAsync();
        return app;
    }

    private static async Task<string?> SelfHrefAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        var json = await client.GetStringAsync(path);
        return JsonDocument.Parse(json).RootElement
            .GetProperty("_links").GetProperty("self").GetProperty("href").GetString();
    }

    private sealed record VersionedOrder(int Id);

    private sealed class VersionedOrderLinks : LinkConfig<VersionedOrder>
    {
        public override void Configure(ILinkBuilder<VersionedOrder> builder)
            => builder.Self(order => LinkTarget.Route("V_GetOrder", new { id = order.Id }));
    }
}
