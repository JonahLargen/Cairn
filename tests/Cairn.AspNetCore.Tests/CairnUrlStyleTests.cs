using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

public class CairnUrlStyleTests
{
    [Fact]
    public async Task Path_relative_style_emits_links_without_scheme_or_host()
    {
        await using var app = await BuildAppAsync(o => o.UrlStyle = LinkUrlStyle.PathRelative);
        using var client = app.GetTestClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/things/7"));
        var self = doc.RootElement.GetProperty("_links").GetProperty("self").GetProperty("href").GetString();

        Assert.Equal("/things/7", self);
    }

    [Fact]
    public async Task Public_base_uri_replaces_the_request_origin_on_route_links()
    {
        await using var app = await BuildAppAsync(o => o.PublicBaseUri = new Uri("https://api.example.com"));
        using var client = app.GetTestClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/things/7"));
        var self = doc.RootElement.GetProperty("_links").GetProperty("self").GetProperty("href").GetString();

        Assert.Equal("https://api.example.com/things/7", self);
    }

    [Fact]
    public async Task Public_base_uri_with_a_path_becomes_the_links_path_base()
    {
        await using var app = await BuildAppAsync(o => o.PublicBaseUri = new Uri("https://api.example.com/v2/"));
        using var client = app.GetTestClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/things/7"));
        var self = doc.RootElement.GetProperty("_links").GetProperty("self").GetProperty("href").GetString();

        Assert.Equal("https://api.example.com/v2/things/7", self);
    }

    [Fact]
    public async Task Pagination_links_honor_the_public_base_uri()
    {
        await using var app = await BuildAppAsync(o => o.PublicBaseUri = new Uri("https://api.example.com"));
        using var client = app.GetTestClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/things?page=2"));
        var links = doc.RootElement.GetProperty("_links");

        Assert.Equal("https://api.example.com/things?page=2", links.GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("https://api.example.com/things?page=1", links.GetProperty("first").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Pagination_links_honor_the_path_relative_style()
    {
        await using var app = await BuildAppAsync(o => o.UrlStyle = LinkUrlStyle.PathRelative);
        using var client = app.GetTestClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/things?page=2"));
        var links = doc.RootElement.GetProperty("_links");

        Assert.Equal("/things?page=2", links.GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("/things?page=3", links.GetProperty("next").GetProperty("href").GetString());
    }

    [Fact]
    public void A_relative_public_base_uri_is_rejected()
    {
        var options = new CairnOptions();
        Assert.Throws<ArgumentException>(() => options.PublicBaseUri = new Uri("/v2", UriKind.Relative));
    }

    private static async Task<WebApplication> BuildAppAsync(Action<CairnOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            configure(o);
            o.AddLinks(new ThingLinks());
        });

        var app = builder.Build();
        app.MapGet("/things/{id:int}", (int id) => TypedResults.Ok(new Thing(id))).WithName("GetThingStyle").WithLinks();
        app.MapGet("/things", (int page = 1) => TypedResults.Ok(
            new PagedResource<Thing>([new Thing(1)], page, PageSize: 1, TotalCount: 5))).WithLinks();

        await app.StartAsync();
        return app;
    }

    private sealed record Thing(int Id);

    private sealed class ThingLinks : LinkConfig<Thing>
    {
        public override void Configure(ILinkBuilder<Thing> builder)
            => builder.Self(r => LinkTarget.Route("GetThingStyle", new { id = r.Id }));
    }
}
