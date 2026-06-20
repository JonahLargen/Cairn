using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnFormatTests
{
    [Fact]
    public async Task Default_format_emits_links_and_actions()
    {
        await using var app = await StartAsync(_ => { });

        var root = await GetJsonAsync(app.Client, "/o/42");

        Assert.True(root.TryGetProperty("_links", out _));
        Assert.True(root.TryGetProperty("_actions", out _));
        Assert.False(root.TryGetProperty("_templates", out _));
    }

    [Fact]
    public async Task Hal_format_emits_links_only_with_hal_content_type()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.Hal);

        var response = await app.Client.GetAsync("/o/42");
        var root = await ReadJsonAsync(response);

        Assert.True(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
        Assert.False(root.TryGetProperty("_templates", out _));
        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HalForms_format_emits_templates_with_hal_forms_content_type()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.HalForms);

        var response = await app.Client.GetAsync("/o/42");
        var root = await ReadJsonAsync(response);

        Assert.False(root.TryGetProperty("_actions", out _));
        var cancel = root.GetProperty("_templates").GetProperty("cancel");
        Assert.Equal("POST", cancel.GetProperty("method").GetString());
        Assert.EndsWith("/o/42/cancel", cancel.GetProperty("target").GetString());
        Assert.Equal("application/prs.hal-forms+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Accept_header_negotiates_hal()
    {
        await using var app = await StartAsync(_ => { });
        app.Client.DefaultRequestHeaders.Accept.ParseAdd("application/hal+json");

        var response = await app.Client.GetAsync("/o/42");
        var root = await ReadJsonAsync(response);

        Assert.False(root.TryGetProperty("_actions", out _));
        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Per_endpoint_format_overrides_accept_and_default()
    {
        await using var app = await StartAsync(_ => { }, forceHalForms: true);
        app.Client.DefaultRequestHeaders.Accept.ParseAdd("application/hal+json");

        var response = await app.Client.GetAsync("/o/42");
        var root = await ReadJsonAsync(response);

        // Forced HAL-FORMS wins over both the Accept header (HAL) and the default.
        Assert.True(root.TryGetProperty("_templates", out _));
        Assert.Equal("application/prs.hal-forms+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<TestApp> StartAsync(Action<CairnOptions> configure, bool forceHalForms = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new FmtOrderLinks());
            configure(o);
        });

        var app = builder.Build();
        app.MapPost("/o/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("FmtCancel");
        var endpoint = app.MapGet("/o/{id:int}", (int id) => TypedResults.Ok(new FmtOrder(id, "Pending")))
            .WithName("FmtOrderById")
            .WithLinks();

        if (forceHalForms)
        {
            endpoint.WithHypermediaFormat(HypermediaFormat.HalForms);
        }

        await app.StartAsync();
        return new TestApp(app, app.GetTestClient());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
        => await ReadJsonAsync(await client.GetAsync(path));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed record FmtOrder(int Id, string Status);

    private sealed class FmtOrderLinks : LinkConfig<FmtOrder>
    {
        public override void Configure(ILinkBuilder<FmtOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("FmtOrderById", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("FmtCancel", new { id = order.Id })).Method("POST");
        }
    }

    private sealed class TestApp(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}
