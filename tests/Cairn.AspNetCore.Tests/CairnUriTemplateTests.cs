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
