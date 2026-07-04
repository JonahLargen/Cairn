using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Every media type Cairn negotiates by is configurable through CairnOptions.MediaTypes.
public class CairnMediaTypeConfigTests
{
    [Fact]
    public async Task A_custom_hal_media_type_selects_and_labels_hal()
    {
        await using var app = await StartAsync(o => o.MediaTypes.Hal = "application/vnd.acme.hal+json");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.acme.hal+json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/vnd.acme.hal+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task A_custom_vendor_token_reaches_the_flat_shape_under_opt_in()
    {
        await using var app = await StartAsync(o =>
        {
            o.DefaultFormat = HypermediaFormat.None;
            o.MediaTypes.Cairn = "application/vnd.acme+json";
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.acme+json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/vnd.acme+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
        Assert.True(root.GetProperty("_actions").TryGetProperty("cancel", out _));

        // The default vendor token no longer applies once it was overridden.
        using var stale = app.GetTestClient();
        stale.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.cairn+json");
        var bare = await stale.GetAsync("/orders/42");
        var bareRoot = JsonDocument.Parse(await bare.Content.ReadAsStringAsync()).RootElement;
        Assert.False(bareRoot.TryGetProperty("_links", out _));
    }

    [Fact]
    public async Task A_custom_plain_token_becomes_the_flat_shapes_media_type_in_default_mode()
    {
        await using var app = await StartAsync(o => o.MediaTypes.Json = "application/vnd.acme+json");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.acme+json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/vnd.acme+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(root.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Opt_in_can_name_both_the_bare_and_the_flat_media_types()
    {
        await using var app = await StartAsync(o =>
        {
            o.DefaultFormat = HypermediaFormat.None;
            o.MediaTypes.Json = "application/vnd.acme+json";        // the "normal" (bare) type
            o.MediaTypes.Cairn = "application/vnd.acme.full+json";  // the flat (linked) type
        });

        using var bareClient = app.GetTestClient();
        bareClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.acme+json");
        var bare = await bareClient.GetAsync("/orders/42");
        var bareRoot = JsonDocument.Parse(await bare.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("application/vnd.acme+json", bare.Content.Headers.ContentType?.MediaType);
        Assert.False(bareRoot.TryGetProperty("_links", out _));

        using var fullClient = app.GetTestClient();
        fullClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.acme.full+json");
        var full = await fullClient.GetAsync("/orders/42");
        var fullRoot = JsonDocument.Parse(await full.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("application/vnd.acme.full+json", full.Content.Headers.ContentType?.MediaType);
        Assert.True(fullRoot.GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task Colliding_media_type_tokens_fail_at_startup()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(o => o.MediaTypes.Cairn = "application/json"));   // collides with Json

        Assert.Contains("distinct media type", thrown.Message);
    }

    [Fact]
    public async Task A_formatter_colliding_with_a_built_in_token_fails_at_startup()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(o => o.MediaTypes.Cairn = "application/vnd.siren+json", registerSiren: true));

        Assert.Contains("collides with the built-in", thrown.Message);
    }

    [Fact]
    public void An_invalid_media_type_is_rejected_when_set()
    {
        Assert.Throws<ArgumentException>(() => new CairnMediaTypeOptions().Hal = "*/*");                     // all types
        Assert.Throws<ArgumentException>(() => new CairnMediaTypeOptions().HalForms = "application/*");      // all subtypes
        Assert.Throws<ArgumentException>(() => new CairnMediaTypeOptions().Cairn = "application/json; v=2"); // parameters
        Assert.Throws<ArgumentException>(() => new CairnMediaTypeOptions().Json = "not a media type");       // unparseable
        Assert.Throws<ArgumentException>(() => new CairnMediaTypeOptions().Json = "   ");                    // whitespace
    }

    private static async Task<WebApplication> StartAsync(Action<CairnOptions>? configure = null, bool registerSiren = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new CfgOrderLinks());
            if (registerSiren)
            {
                o.AddFormatter(new SirenStub());
            }

            configure?.Invoke(o);
        });

        var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new CfgOrder(id))).WithName("CfgGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("CfgCancel");
        await app.StartAsync();
        return app;
    }

    private sealed record CfgOrder(int Id);

    private sealed class CfgOrderLinks : LinkConfig<CfgOrder>
    {
        public override void Configure(ILinkBuilder<CfgOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("CfgGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("CfgCancel", new { id = order.Id })).Method("POST");
        }
    }

    private sealed class SirenStub : IHypermediaFormatter
    {
        public string MediaType => "application/vnd.siren+json";

        public IReadOnlyList<HypermediaFormatProperty> Properties => [];
    }
}
