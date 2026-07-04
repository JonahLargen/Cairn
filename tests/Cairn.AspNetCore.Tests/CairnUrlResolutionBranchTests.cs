using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Branch-direction coverage for LinkGeneratorUrlResolver, RoutePatternCache, and PaginationLinks:
// out-of-request resolution, template rendering edges, and pagination edge values.
public class CairnUrlResolutionBranchTests
{
    [Fact]
    public async Task Route_targets_resolve_to_null_outside_a_request_but_explicit_uris_survive()
    {
        await using var app = await StartAsync();

        // Outside a request there is no HttpContext to derive a base URL from.
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ILinkUrlResolver>();

        Assert.Null(resolver.Resolve(LinkTarget.Route("TplItem", new { id = 1, itemId = 2 })));
        Assert.Null(resolver.Resolve(LinkTarget.RouteTemplate("TplItem")));
        Assert.Equal("https://elsewhere.test/x", resolver.Resolve(LinkTarget.Uri("https://elsewhere.test/x")));
    }

    [Fact]
    public async Task Template_links_render_roots_separators_bound_values_and_extras()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/doc/1")).RootElement.GetProperty("_links");

        // The root route has no path segments, so the template collapses to "/".
        Assert.EndsWith("://localhost/", links.GetProperty("root").GetProperty("href").GetString());

        // The optional-extension pattern keeps its literal separator between the two placeholders.
        Assert.EndsWith("/files/{name}.{ext}", links.GetProperty("file").GetProperty("href").GetString());

        // Supplied values bind into the path, nulls are dropped, and leftovers become query parameters.
        Assert.EndsWith("/orders/5/items/{itemId}?q=x", links.GetProperty("item").GetProperty("href").GetString());

        // A route value whose ToString() returns null binds as an empty segment.
        Assert.EndsWith("/weird/", links.GetProperty("weird").GetProperty("href").GetString());

        // A template naming an unknown route is dropped in Lax mode.
        Assert.False(links.TryGetProperty("missing", out _));

        // A second request goes through the cached name → pattern map.
        var again = JsonDocument.Parse(await client.GetStringAsync("/doc/1")).RootElement.GetProperty("_links");
        Assert.EndsWith("/files/{name}.{ext}", again.GetProperty("file").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Template_links_honor_the_path_relative_url_style()
    {
        await using var app = await StartAsync(o => o.UrlStyle = LinkUrlStyle.PathRelative);
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/doc/1")).RootElement.GetProperty("_links");

        Assert.Equal("/orders/5/items/{itemId}?q=x", links.GetProperty("item").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Template_and_route_links_honor_a_public_base_uri_with_a_non_default_port()
    {
        await using var app = await StartAsync(o => o.PublicBaseUri = new Uri("https://edge.example.com:8443/api/"));
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/doc/1")).RootElement.GetProperty("_links");

        // Both resolvers rebase onto the public origin, keeping the explicit port and path base.
        Assert.Equal("https://edge.example.com:8443/api/doc/1", links.GetProperty("self").GetProperty("href").GetString());
        Assert.Equal(
            "https://edge.example.com:8443/api/orders/5/items/{itemId}?q=x",
            links.GetProperty("item").GetProperty("href").GetString());
    }

    [Fact]
    public async Task An_over_range_page_of_an_empty_result_links_only_itself()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/empty", () => TypedResults.Ok(new PagedResource<int>([], Page: 3, PageSize: 10, TotalCount: 0))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/empty")).RootElement.GetProperty("_links");

        // With zero pages there is nothing for first/last/prev/next to point at.
        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(links.TryGetProperty("first", out _));
        Assert.False(links.TryGetProperty("last", out _));
        Assert.False(links.TryGetProperty("prev", out _));
        Assert.False(links.TryGetProperty("next", out _));
    }

    [Fact]
    public async Task Empty_string_cursors_produce_no_cursor_links()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/feed", () => TypedResults.Ok(new CursorPage<int>([1], Next: string.Empty, Prev: string.Empty))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/feed")).RootElement.GetProperty("_links");

        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(links.TryGetProperty("next", out _));
        Assert.False(links.TryGetProperty("prev", out _));
    }

    [Fact]
    public async Task A_paging_view_with_a_zero_page_size_reports_zero_pages()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddPaging<ZeroSizeEnvelope>(e => new PagedView(e.Values, 1, 0, 0)));

        await using var app = builder.Build();
        app.MapGet("/zero", () => TypedResults.Ok(new ZeroSizeEnvelope())).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/zero")).RootElement.GetProperty("_links");

        // TotalPages computes to zero, so only self is emitted.
        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(links.TryGetProperty("last", out _));
    }

    private static async Task<WebApplication> StartAsync(Action<CairnOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new TplDocLinks());
            configure?.Invoke(o);
        });

        var app = builder.Build();
        app.MapGet("/", () => "root").WithName("TplRoot");
        app.MapGet("/files/{name}.{ext?}", (string name, string? ext) => "file").WithName("TplFile");
        app.MapGet("/orders/{id}/items/{itemId}", (string id, string itemId) => "item").WithName("TplItem");
        app.MapGet("/weird/{code?}", (string? code) => "weird").WithName("TplWeird");
        app.MapGet("/doc/{id:int}", (int id) => TypedResults.Ok(new TplDoc(id))).WithName("TplDocGet").WithLinks();

        // Endpoints carrying only one kind of name metadata, so the pattern cache sees each shape.
        app.MapGet("/only-route-name", () => "r").WithMetadata(new RouteNameMetadata("TplOnlyRouteName"));
        app.MapGet("/only-endpoint-name", () => "e").WithMetadata(new EndpointNameMetadata("TplOnlyEndpointName"));

        await app.StartAsync();
        return app;
    }

    private sealed record TplDoc(int Id);

    // A value that stringifies to null, exercising the empty-segment fallback for bound route values.
    private sealed class NullToStringValue
    {
        public override string? ToString() => null;
    }

    private sealed class ZeroSizeEnvelope
    {
        public IReadOnlyList<int> Values { get; } = [];
    }

    private sealed class TplDocLinks : LinkConfig<TplDoc>
    {
        public override void Configure(ILinkBuilder<TplDoc> builder)
        {
            builder.Self(doc => LinkTarget.Route("TplDocGet", new { id = doc.Id }));
            builder.Link("root", _ => LinkTarget.RouteTemplate("TplRoot"));
            builder.Link("file", _ => LinkTarget.RouteTemplate("TplFile"));
            builder.Link("item", _ => LinkTarget.RouteTemplate("TplItem", new { id = 5, q = "x", skip = (string?)null }));
            builder.Link("weird", _ => LinkTarget.RouteTemplate("TplWeird", new { code = new NullToStringValue() }));
            builder.Link("missing", _ => LinkTarget.RouteTemplate("TplNoSuchRoute"));
        }
    }
}
