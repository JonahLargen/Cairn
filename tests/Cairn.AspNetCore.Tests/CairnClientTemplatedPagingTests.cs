using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Templated pagination links (a "next" carrying "{?page}") must stay followable: with no variables every
// unresolved expression collapses per RFC 6570, and variables can be supplied to expand them.
public class CairnClientTemplatedPagingTests
{
    private const string FirstPage = """
        {
          "items": [ { "id": 1 } ],
          "_links": {
            "self": { "href": "/paged" },
            "next": { "href": "/paged/2{?verbose}", "templated": true }
          }
        }
        """;

    private const string SecondPage = """
        {
          "items": [ { "id": 2 } ],
          "_links": { "self": { "href": "/paged/2" } }
        }
        """;

    [Fact]
    public async Task A_templated_next_link_is_followable_with_no_variables_collapsing_its_expressions()
    {
        string? query = null;
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/paged", () => Results.Text(FirstPage, "application/json"));
            a.MapGet("/paged/2", (HttpContext ctx) =>
            {
                query = ctx.Request.QueryString.Value;
                return Results.Text(SecondPage, "application/json");
            });
        });
        using var httpClient = app.GetTestClient();

        var page = (await new CairnClient(httpClient).GetCollectionAsync<PagedThing>("/paged")).EnsureSuccess();
        var next = (await page.FollowAsync("next")).EnsureSuccess();

        Assert.Equal("", query);   // "{?verbose}" collapsed
        Assert.Equal(2, Assert.Single(next.Items).Value!.Id);
    }

    [Fact]
    public async Task A_templated_next_link_expands_supplied_variables()
    {
        string? query = null;
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/paged", () => Results.Text(FirstPage, "application/json"));
            a.MapGet("/paged/2", (HttpContext ctx) =>
            {
                query = ctx.Request.QueryString.Value;
                return Results.Text(SecondPage, "application/json");
            });
        });
        using var httpClient = app.GetTestClient();

        var page = (await new CairnClient(httpClient).GetCollectionAsync<PagedThing>("/paged")).EnsureSuccess();
        var next = (await page.FollowAsync("next", new { verbose = "true" })).EnsureSuccess();

        Assert.Equal("?verbose=true", query);
        Assert.Equal(2, Assert.Single(next.Items).Value!.Id);
    }

    [Fact]
    public async Task Variables_for_a_non_templated_collection_link_throw_instead_of_being_ignored()
    {
        await using var app = await StartAsync(a => a.MapGet("/paged", () => Results.Text(FirstPage, "application/json")));
        using var httpClient = app.GetTestClient();

        var page = (await new CairnClient(httpClient).GetCollectionAsync<PagedThing>("/paged")).EnsureSuccess();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => page.FollowAsync("self", new { page = 2 }));
        Assert.Contains("not templated", exception.Message);
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

    private sealed record PagedThing(int Id);
}
