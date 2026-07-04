using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnLinkHeaderTests
{
    [Fact]
    public async Task No_Link_header_is_emitted_by_default()
    {
        await using var app = await StartAsync(
            emitLinkHeader: false,
            configure: b => b.Self(o => LinkTarget.Route("HdrSelf", new { id = o.Id })));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();

        // The links are still in the body — only the header is opt-in.
        var links = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("_links");
        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(response.Headers.Contains("Link"));
    }

    [Fact]
    public async Task The_top_level_resources_links_are_advertised_as_an_RFC8288_Link_header()
    {
        await using var app = await StartAsync(
            emitLinkHeader: true,
            configure: b =>
            {
                b.Self(o => LinkTarget.Route("HdrSelf", new { id = o.Id }));
                b.Link("next", o => LinkTarget.Route("HdrSelf", new { id = o.Id + 1 }))
                    .Title("The next one")
                    .Type("application/json");
            });
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();

        var link = LinkHeader(response);
        var self = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("_links").GetProperty("self").GetProperty("href").GetString();

        // The header target mirrors the body's self href, verbatim, inside <>.
        Assert.Contains($"<{self}>; rel=\"self\"", link, StringComparison.Ordinal);
        Assert.Contains("rel=\"next\"", link, StringComparison.Ordinal);
        Assert.Contains("title=\"The next one\"", link, StringComparison.Ordinal);
        Assert.Contains("type=\"application/json\"", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_templated_link_is_omitted_from_the_Link_header()
    {
        await using var app = await StartAsync(
            emitLinkHeader: true,
            configure: b =>
            {
                b.Self(o => LinkTarget.Route("HdrSelf", new { id = o.Id }));
                b.Link("search", _ => LinkTarget.Uri("/h{?q}", templated: true));
            });
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();

        // The templated link is present in the body but not a valid URI-Reference for a header target.
        var links = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("_links");
        Assert.True(links.GetProperty("search").GetProperty("templated").GetBoolean());

        var link = LinkHeader(response);
        Assert.Contains("rel=\"self\"", link, StringComparison.Ordinal);
        Assert.DoesNotContain("rel=\"search\"", link, StringComparison.Ordinal);
        Assert.DoesNotContain("{?q}", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_href_that_cannot_be_a_header_target_is_skipped_not_emitted_malformed()
    {
        await using var app = await StartAsync(
            emitLinkHeader: true,
            configure: b =>
            {
                b.Self(o => LinkTarget.Route("HdrSelf", new { id = o.Id }));
                b.Link("weird", _ => LinkTarget.Uri("http://example.com/a>b"));
            });
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();

        var link = LinkHeader(response);
        Assert.Contains("rel=\"self\"", link, StringComparison.Ordinal);
        // The '>' would close the target early — the link is dropped rather than corrupting the header.
        Assert.DoesNotContain("rel=\"weird\"", link, StringComparison.Ordinal);
        Assert.DoesNotContain("a>b", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quote_in_a_title_is_escaped_per_the_quoted_string_grammar()
    {
        await using var app = await StartAsync(
            emitLinkHeader: true,
            configure: b => b.Self(o => LinkTarget.Route("HdrSelf", new { id = o.Id })).Title("He said \"hi\""));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();

        // RFC 7230 quoted-string: the embedded double-quotes are backslash-escaped.
        Assert.Contains("title=\"He said \\\"hi\\\"\"", LinkHeader(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_characters_in_an_attribute_are_dropped_so_the_header_cannot_be_injected()
    {
        await using var app = await StartAsync(
            emitLinkHeader: true,
            configure: b => b.Self(o => LinkTarget.Route("HdrSelf", new { id = o.Id })).Title("a\r\nb"));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();

        var link = LinkHeader(response);
        Assert.Contains("title=\"ab\"", link, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', link);
        Assert.DoesNotContain('\r', link);
    }

    [Fact]
    public async Task Pagination_links_of_a_paged_envelope_appear_in_the_Link_header()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.EmitLinkHeader = true);

        await using var app = builder.Build();
        app.MapGet("/p", () => TypedResults.Ok(new PagedResource<int>([1, 2], Page: 1, PageSize: 2, TotalCount: 10)))
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/p");
        response.EnsureSuccessStatusCode();

        var link = LinkHeader(response);
        Assert.Contains("rel=\"self\"", link, StringComparison.Ordinal);
        Assert.Contains("rel=\"next\"", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bare_collection_emits_no_top_level_Link_header()
    {
        await using var app = await StartAsync(
            emitLinkHeader: true,
            configure: b => b.Self(o => LinkTarget.Uri($"/h/{o.Id}")),
            map: app => app.MapGet("/all", () => TypedResults.Ok(new[] { new HdrResource(1), new HdrResource(2) })).WithLinks());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/all");
        response.EnsureSuccessStatusCode();

        // Each element carries _links in the body, but a JSON array has no context resource — so no header
        // (dumping every element's self link would multiply the header set without a primary target).
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body[0].GetProperty("_links").TryGetProperty("self", out _));
        Assert.False(response.Headers.Contains("Link"));
    }

    [Fact]
    public async Task The_body_link_header_composes_with_the_deprecation_link_header()
    {
        await using var app = await StartAsync(
            emitLinkHeader: true,
            configure: b => b.Self(o => LinkTarget.Route("HdrSelf", new { id = o.Id })),
            map: app => app.MapGet("/h/{id:int}", (int id) => TypedResults.Ok(new HdrResource(id)))
                .WithName("HdrSelf")
                .WithLinks()
                .WithDeprecation(link: "https://docs.example.com/deprecations/h"));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();

        var link = LinkHeader(response);
        Assert.Contains("rel=\"self\"", link, StringComparison.Ordinal);
        Assert.Contains("rel=\"deprecation\"", link, StringComparison.Ordinal);
        Assert.Contains("https://docs.example.com/deprecations/h", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_Link_header_when_no_hypermedia_is_negotiated()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.EmitLinkHeader = true;
            o.DefaultFormat = HypermediaFormat.None;   // links become opt-in by the caller's Accept header
            o.AddLinks(new HdrLinks(b => b.Self(x => LinkTarget.Route("HdrSelf", new { id = x.Id }))));
        });

        await using var app = builder.Build();
        app.MapGet("/h/{id:int}", (int id) => TypedResults.Ok(new HdrResource(id))).WithName("HdrSelf").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        // Plain application/json negotiates no hypermedia, so there are no body links to advertise.
        using var response = await client.GetAsync("/h/1");
        response.EnsureSuccessStatusCode();
        Assert.False(response.Headers.Contains("Link"));
    }

    // Every Link header line joined, so an assertion is agnostic to whether the client split a comma-separated
    // value into several (e.g. our body links and a separate deprecation link line).
    private static string LinkHeader(HttpResponseMessage response)
        => string.Join(", ", response.Headers.GetValues("Link"));

    private static Task<WebApplication> StartAsync(bool emitLinkHeader, Action<ILinkBuilder<HdrResource>> configure)
        => StartAsync(emitLinkHeader, configure, app =>
            app.MapGet("/h/{id:int}", (int id) => TypedResults.Ok(new HdrResource(id))).WithName("HdrSelf").WithLinks());

    private static async Task<WebApplication> StartAsync(
        bool emitLinkHeader,
        Action<ILinkBuilder<HdrResource>> configure,
        Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.EmitLinkHeader = emitLinkHeader;
            o.AddLinks(new HdrLinks(configure));
        });

        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    private sealed record HdrResource(int Id);

    private sealed class HdrLinks(Action<ILinkBuilder<HdrResource>> configure) : LinkConfig<HdrResource>
    {
        public override void Configure(ILinkBuilder<HdrResource> builder) => configure(builder);
    }
}
