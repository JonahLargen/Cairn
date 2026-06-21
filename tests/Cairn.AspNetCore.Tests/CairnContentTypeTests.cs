using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnContentTypeTests
{
    [Fact]
    public async Task Relabel_preserves_a_media_type_version_parameter()
    {
        // Media-type API versioning (e.g. Asp.Versioning's `v` parameter) composes with HAL: the version survives.
        await using var app = await StartHalAsync(a =>
            a.MapGet("/o/{id:int}", (int id) => Results.Json(new CtOrder(id), contentType: "application/json; v=2")).WithName("CtGet").WithLinks());
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/o/42");

        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(response.Content.Headers.ContentType!.Parameters, p => p.Name == "v" && p.Value == "2");
    }

    [Fact]
    public async Task Relabel_preserves_charset()
    {
        await using var app = await StartHalAsync(a =>
            a.MapGet("/o/{id:int}", (int id) => Results.Json(new CtOrder(id), contentType: "application/json; charset=utf-8")).WithName("CtGet").WithLinks());
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/o/42");

        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
    }

    [Fact]
    public async Task An_explicit_vendor_media_type_is_left_untouched()
    {
        await using var app = await StartHalAsync(a =>
            a.MapGet("/o/{id:int}", (int id) => Results.Json(new CtOrder(id), contentType: "application/vnd.acme.order+json")).WithName("CtGet").WithLinks());
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/o/42");
        var body = await response.Content.ReadAsStringAsync();

        // Cairn only claims plain application/json; an explicit vendor type is deferred to (links are still injected).
        Assert.Equal("application/vnd.acme.order+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("_links", body);
    }

    private static async Task<WebApplication> StartHalAsync(Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new CtOrderLinks());
            o.DefaultFormat = HypermediaFormat.Hal;
        });

        var app = builder.Build();
        endpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed record CtOrder(int Id);

    private sealed class CtOrderLinks : LinkConfig<CtOrder>
    {
        public override void Configure(ILinkBuilder<CtOrder> builder)
            => builder.Self(order => LinkTarget.Route("CtGet", new { id = order.Id }));
    }
}
