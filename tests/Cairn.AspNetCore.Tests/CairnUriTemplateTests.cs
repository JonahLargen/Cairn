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

    [Fact]
    public async Task Supplying_variables_for_a_non_templated_link_throws_instead_of_ignoring_them()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost") };
        var client = new CairnClient(http);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.FollowAsync<JsonElement>(new Link("self", "/plain"), new { page = 2 }));

        Assert.Contains("not templated", exception.Message);
    }

    [Fact]
    public async Task Simple_expansion_percent_encodes_astral_characters_as_utf8()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("item", "/items/{name}", templated: true), new { name = "😀" });

        Assert.Contains("%F0%9F%98%80", handler.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("%EF%BF%BD", handler.RequestUri.AbsoluteUri);   // no U+FFFD corruption
    }

    [Fact]
    public async Task Reserved_expansion_percent_encodes_astral_characters_as_utf8()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("file", "/files{+path}", templated: true), new { path = "/docs/😀.txt" });

        Assert.Contains("/files/docs/%F0%9F%98%80.txt", handler.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("%EF%BF%BD", handler.RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task Fragment_expansion_percent_encodes_astral_characters_as_utf8()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("doc", "/doc{#section}", templated: true), new { section = "intro😀" });

        Assert.Contains("#intro%F0%9F%98%80", handler.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("%EF%BF%BD", handler.RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task Reserved_expansion_encodes_a_bare_percent_as_a_triplet()
    {
        var (client, handler) = NewRecordingClient();

        // RFC 6570 §3.2.3: only a valid pct-triplet passes through; a bare '%' is data. Without this,
        // "50% off" expands to the invalid URI "50%%20off".
        await client.FollowAsync<JsonElement>(new Link("file", "/files/{+name}", templated: true), new { name = "50% off" });

        Assert.Contains("/files/50%25%20off", handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Reserved_expansion_passes_a_valid_pct_triplet_through()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("file", "/files/{+name}", templated: true), new { name = "a%20b" });

        // The existing escape survives untouched — not double-encoded to a%2520b.
        Assert.Contains("/files/a%20b", handler.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("%2520", handler.RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task Fragment_expansion_encodes_a_bare_percent_as_a_triplet()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("doc", "/doc{#section}", templated: true), new { section = "50% off" });

        Assert.Contains("#50%25%20off", handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Prefix_modifier_counts_code_points_not_utf16_units()
    {
        var (client, handler) = NewRecordingClient();

        // {key:1} must take the whole first character: slicing UTF-16 units would split the surrogate pair
        // and produce U+FFFD replacement bytes (%EF%BF%BD).
        await client.FollowAsync<JsonElement>(new Link("item", "/items/{key:1}", templated: true), new { key = "😀abc" });

        Assert.EndsWith("/items/%F0%9F%98%80", handler.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("%EF%BF%BD", handler.RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task Prefix_modifier_counts_an_astral_character_as_one()
    {
        var (client, handler) = NewRecordingClient();

        await client.FollowAsync<JsonElement>(new Link("item", "/items/{key:2}", templated: true), new { key = "😀abc" });

        Assert.EndsWith("/items/%F0%9F%98%80a", handler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Bools_and_dates_expand_in_wire_format_not_dotnet_display_format()
    {
        var (client, handler) = NewRecordingClient();
        var link = new Link("search", "/search{?active,since}", templated: true);

        await client.FollowAsync<JsonElement>(link, new { active = true, since = new DateTime(2026, 7, 3, 8, 30, 0, DateTimeKind.Utc) });

        // Lowercase bools and round-trip ("O") dates survive a server-side parse; "True" and
        // "07/03/2026 08:30:00" do not.
        Assert.Equal("?active=true&since=2026-07-03T08:30:00.0000000Z", Uri.UnescapeDataString(handler.RequestUri!.Query));
    }

    private static (CairnClient Client, RecordingHandler Handler) NewRecordingClient()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return (new CairnClient(http), handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
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
