using System.Text.Json;
using Cairn;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnUriTemplateTests
{
    [Fact]
    public async Task Follows_a_templated_link_expanding_query_variables()
    {
        string? query = null;
        await using var app = await StartAsync(a => a.MapGet("/search", (HttpContext ctx) =>
        {
            query = ctx.Request.QueryString.Value;
            return Results.Json(new { ok = true });
        }));
        using var httpClient = app.GetTestClient();

        var link = new Link("search", "/search{?status,page}", templated: true);
        var result = await new CairnClient(httpClient).FollowAsync<JsonElement>(link, new { status = "open", page = 2 });

        Assert.True(result.IsSuccess);
        Assert.Equal("?status=open&page=2", query);
    }

    [Fact]
    public async Task Expands_a_simple_path_variable()
    {
        string? path = null;
        await using var app = await StartAsync(a => a.MapGet("/items/{id:int}", (int id, HttpContext ctx) =>
        {
            path = ctx.Request.Path;
            return Results.Json(new { id });
        }));
        using var httpClient = app.GetTestClient();

        var result = await new CairnClient(httpClient).FollowAsync<JsonElement>(new Link("item", "/items/{id}", templated: true), new { id = 42 });

        Assert.True(result.IsSuccess);
        Assert.Equal("/items/42", path);
    }

    [Fact]
    public async Task Expands_variables_from_a_string_dictionary()
    {
        string? query = null;
        await using var app = await StartAsync(a => a.MapGet("/search", (HttpContext ctx) =>
        {
            query = ctx.Request.QueryString.Value;
            return Results.Json(new { ok = true });
        }));
        using var httpClient = app.GetTestClient();

        var link = new Link("search", "/search{?status,page}", templated: true);
        var variables = new Dictionary<string, string> { ["status"] = "open", ["page"] = "2" };
        var result = await new CairnClient(httpClient).FollowAsync<JsonElement>(link, variables);

        Assert.True(result.IsSuccess);
        Assert.Equal("?status=open&page=2", query);
    }

    [Fact]
    public async Task Expands_list_variables_and_explode_modifier()
    {
        string? query = null;
        await using var app = await StartAsync(a => a.MapGet("/search", (HttpContext ctx) =>
        {
            query = ctx.Request.QueryString.Value;
            return Results.Json(new { ok = true });
        }));
        using var httpClient = app.GetTestClient();

        var link = new Link("search", "/search{?tags*,ids}", templated: true);
        var result = await new CairnClient(httpClient).FollowAsync<JsonElement>(
            link,
            new { tags = new[] { "red", "blue" }, ids = new[] { 1, 2 } });

        Assert.True(result.IsSuccess);
        Assert.Equal("?tags=red&tags=blue&ids=1,2", Uri.UnescapeDataString(query!));
    }

    [Fact]
    public async Task Applies_prefix_modifier_instead_of_dropping_it()
    {
        string? path = null;
        await using var app = await StartAsync(a => a.MapGet("/items/{key}", (string key, HttpContext ctx) =>
        {
            path = ctx.Request.Path;
            return Results.Json(new { key });
        }));
        using var httpClient = app.GetTestClient();

        var result = await new CairnClient(httpClient).FollowAsync<JsonElement>(
            new Link("item", "/items/{key:3}", templated: true),
            new { key = "abcdef" });

        Assert.True(result.IsSuccess);
        Assert.Equal("/items/abc", path);
    }

    [Fact]
    public async Task Following_a_templated_link_without_variables_throws()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost") };
        var client = new CairnClient(http);

        await Assert.ThrowsAsync<NotSupportedException>(() => client.FollowAsync<JsonElement>(new Link("search", "/s{?q}", templated: true)));
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        endpoints(app);
        await app.StartAsync();
        return app;
    }
}
