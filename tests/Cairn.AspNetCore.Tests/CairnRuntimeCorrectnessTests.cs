using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnRuntimeCorrectnessTests
{
    [Fact]
    public async Task Duplicate_link_relation_does_not_crash_and_last_wins()
    {
        await using var app = await StartAsync(
            services => services.AddCairn(o => o.AddLinks(new DuplicateLinkConfig())),
            endpoints => endpoints.MapGet("/dup/{id:int}", (int id) => TypedResults.Ok(new Widget(id)))
                .WithName("DupWidget").WithLinks());

        var root = await GetJsonAsync(app, "/dup/5");

        Assert.EndsWith("/second/5", root.GetProperty("_links").GetProperty("alternate").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Duplicate_affordance_name_does_not_crash_and_last_wins()
    {
        await using var app = await StartAsync(
            services => services.AddCairn(o => o.AddLinks(new DuplicateAffordanceConfig())),
            endpoints => endpoints.MapGet("/dupa/{id:int}", (int id) => TypedResults.Ok(new Widget(id)))
                .WithName("DupActionWidget").WithLinks());

        var root = await GetJsonAsync(app, "/dupa/5");

        Assert.Equal("DELETE", root.GetProperty("_actions").GetProperty("cancel").GetProperty("method").GetString());
    }

    [Fact]
    public async Task Dto_that_already_declares_links_serializes_without_collision()
    {
        await using var app = await StartAsync(
            services => services.AddCairn(o => o.AddLinks(new SelfOnlyConfig())),
            endpoints => endpoints.MapGet("/own/{id:int}", (int id) => TypedResults.Ok(new HandRolled(id, "mine")))
                .WithName("HandRolledById").WithLinks());

        var root = await GetJsonAsync(app, "/own/5");

        // No serialization crash; the DTO's own _links is preserved (Cairn skips the colliding name).
        Assert.Equal("mine", root.GetProperty("_links").GetString());
    }

    [Fact]
    public async Task Offset_prev_link_is_clamped_to_the_last_valid_page()
    {
        await using var app = await StartAsync(
            services => services.AddCairn(),
            endpoints => endpoints.MapGet("/items", (int page) => TypedResults.Ok(
                    new PagedResource<Widget>([], page, PageSize: 10, TotalCount: 25)))   // TotalPages = 3
                .WithLinks());

        var root = await GetJsonAsync(app, "/items?page=5");
        var links = root.GetProperty("_links");

        Assert.EndsWith("page=3", links.GetProperty("prev").GetProperty("href").GetString());
        Assert.False(links.TryGetProperty("next", out _));
    }

    private static async Task<WebApplication> StartAsync(Action<IServiceCollection> configureServices, Action<WebApplication> configureEndpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configureServices(builder.Services);

        var app = builder.Build();
        configureEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static async Task<JsonElement> GetJsonAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        var json = await client.GetStringAsync(path);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed record Widget(int Id);

    private sealed record HandRolled(int Id, [property: JsonPropertyName("_links")] string Links);

    private sealed class DuplicateLinkConfig : LinkConfig<Widget>
    {
        public override void Configure(ILinkBuilder<Widget> builder)
        {
            builder.Self(w => LinkTarget.Route("DupWidget", new { id = w.Id }));
            builder.Link("alternate", w => LinkTarget.Uri($"/first/{w.Id}"));
            builder.Link("alternate", w => LinkTarget.Uri($"/second/{w.Id}"));
        }
    }

    private sealed class DuplicateAffordanceConfig : LinkConfig<Widget>
    {
        public override void Configure(ILinkBuilder<Widget> builder)
        {
            builder.Self(w => LinkTarget.Route("DupActionWidget", new { id = w.Id }));
            builder.Affordance("cancel", w => LinkTarget.Uri($"/x/{w.Id}")).Method("POST");
            builder.Affordance("cancel", w => LinkTarget.Uri($"/x/{w.Id}")).Method("DELETE");
        }
    }

    private sealed class SelfOnlyConfig : LinkConfig<HandRolled>
    {
        public override void Configure(ILinkBuilder<HandRolled> builder)
            => builder.Self(h => LinkTarget.Route("HandRolledById", new { id = h.Id }));
    }
}
