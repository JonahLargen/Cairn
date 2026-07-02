using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

public class CairnFormatterTests
{
    [Fact]
    public async Task A_custom_formatter_is_negotiated_by_accept_and_supersedes_the_built_in_emission()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.siren+json"));

        var response = await client.GetAsync("/fmt/1");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("application/vnd.siren+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(body);
        var links = doc.RootElement.GetProperty("links");
        var link = Assert.Single(links.EnumerateArray());
        Assert.Equal("self", link.GetProperty("rel")[0].GetString());
        Assert.Equal("cancel", Assert.Single(doc.RootElement.GetProperty("actions").EnumerateArray()).GetProperty("name").GetString());
        Assert.False(doc.RootElement.TryGetProperty("_links", out _));
        Assert.False(doc.RootElement.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Without_a_matching_accept_the_built_in_format_still_emits()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/fmt/1"));

        Assert.True(doc.RootElement.TryGetProperty("_links", out _));
        Assert.True(doc.RootElement.TryGetProperty("_actions", out _));
        Assert.False(doc.RootElement.TryGetProperty("links", out _));
    }

    [Fact]
    public async Task A_custom_format_can_be_forced_per_endpoint_by_media_type()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/fmt-forced/1");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("application/vnd.siren+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(doc.RootElement.TryGetProperty("links", out _));
    }

    [Fact]
    public async Task A_higher_quality_built_in_ask_beats_the_custom_media_type()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.siren+json;q=0.5, application/hal+json");

        var response = await client.GetAsync("/fmt/1");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(doc.RootElement.TryGetProperty("_links", out _));
        Assert.False(doc.RootElement.TryGetProperty("links", out _));
    }

    [Fact]
    public async Task Forcing_an_unregistered_media_type_fails_loudly()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new FmtLinks()));

        await using var app = builder.Build();
        app.MapGet("/fmt/{id:int}", (int id) => TypedResults.Ok(new FmtResource(id)))
            .WithName("FmtMissingSelf")
            .WithLinks()
            .WithHypermediaFormat("application/vnd.unknown+json");
        await app.StartAsync();
        using var client = app.GetTestClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/fmt/1"));
    }

    [Fact]
    public void Duplicate_formatter_media_types_are_rejected()
    {
        var options = new CairnOptions();
        options.AddFormatter(new SirenFormatter());
        Assert.Throws<ArgumentException>(() => options.AddFormatter(new SirenFormatter()));
    }

    private static async Task<WebApplication> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new FmtLinks());
            o.AddFormatter(new SirenFormatter());
        });

        var app = builder.Build();
        app.MapGet("/fmt/{id:int}", (int id) => TypedResults.Ok(new FmtResource(id))).WithName("FmtSelf").WithLinks();
        app.MapPost("/fmt/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("FmtCancel");
        app.MapGet("/fmt-forced/{id:int}", (int id) => TypedResults.Ok(new FmtResource(id)))
            .WithLinks()
            .WithHypermediaFormat("application/vnd.siren+json");
        await app.StartAsync();
        return app;
    }

    private sealed record FmtResource(int Id);

    private sealed class FmtLinks : LinkConfig<FmtResource>
    {
        public override void Configure(ILinkBuilder<FmtResource> builder)
        {
            builder.Self(r => LinkTarget.Route("FmtSelf", new { id = r.Id }));
            builder.Affordance("cancel", r => LinkTarget.Route("FmtCancel", new { id = r.Id })).Post();
        }
    }

    // A miniature Siren projection: links as [{rel: [..], href}], affordances as [{name, method, href}].
    private sealed class SirenFormatter : IHypermediaFormatter
    {
        public string MediaType => "application/vnd.siren+json";

        public IReadOnlyList<HypermediaFormatProperty> Properties { get; } =
        [
            new("links", document => document.Links.Count == 0
                ? null
                : document.Links.Select(link => new Dictionary<string, object>
                {
                    ["rel"] = new[] { link.Relation.Value },
                    ["href"] = link.Href,
                }).ToList()),
            new("actions", document => document.Affordances.Count == 0
                ? null
                : document.Affordances.Select(affordance => new Dictionary<string, object>
                {
                    ["name"] = affordance.Name.Value,
                    ["method"] = affordance.Method,
                    ["href"] = affordance.Href,
                }).ToList()),
        ];
    }
}
