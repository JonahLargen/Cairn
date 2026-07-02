using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnRouteTemplateLinkTests
{
    [Fact]
    public async Task An_unbound_route_parameter_emits_an_rfc_6570_placeholder_and_templated_true()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new SearchTemplateLinks()));

        await using var app = builder.Build();
        app.MapGet("/rt/{id:int}", (int id) => TypedResults.Ok(new TemplateOrder(id))).WithName("RtGetOrder").WithLinks();
        app.MapGet("/rt/search/{term}", (string term) => TypedResults.Ok(new { term })).WithName("RtSearch");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/rt/7")).RootElement.GetProperty("_links");

        var search = links.GetProperty("search");
        Assert.EndsWith("/rt/search/{term}", search.GetProperty("href").GetString());
        Assert.StartsWith("http://", search.GetProperty("href").GetString());
        Assert.True(search.GetProperty("templated").GetBoolean());
    }

    [Fact]
    public async Task Supplied_route_values_are_bound_and_the_rest_stay_placeholders()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new NoteTemplateLinks()));

        await using var app = builder.Build();
        app.MapGet("/rn/{id:int}", (int id) => TypedResults.Ok(new TemplateOrder(id))).WithName("RnGetOrder").WithLinks();
        app.MapGet("/rn/{id:int}/notes/{noteId}", (int id, string noteId) => TypedResults.Ok(new { id, noteId })).WithName("RnGetNote");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/rn/5")).RootElement.GetProperty("_links");

        var note = links.GetProperty("note");
        Assert.EndsWith("/rn/5/notes/{noteId}", note.GetProperty("href").GetString());
        Assert.True(note.GetProperty("templated").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_route_name_is_dropped_in_lax_mode()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new MissingTemplateLinks()));

        await using var app = builder.Build();
        app.MapGet("/rm/{id:int}", (int id) => TypedResults.Ok(new TemplateOrder(id))).WithName("RmGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/rm/7")).RootElement.GetProperty("_links");

        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(links.TryGetProperty("search", out _));
    }

    private sealed record TemplateOrder(int Id);

    private sealed class SearchTemplateLinks : LinkConfig<TemplateOrder>
    {
        public override void Configure(ILinkBuilder<TemplateOrder> builder)
        {
            builder.Self(o => LinkTarget.Route("RtGetOrder", new { id = o.Id }));
            builder.Link("search", _ => LinkTarget.RouteTemplate("RtSearch"));
        }
    }

    private sealed class NoteTemplateLinks : LinkConfig<TemplateOrder>
    {
        public override void Configure(ILinkBuilder<TemplateOrder> builder)
        {
            builder.Self(o => LinkTarget.Route("RnGetOrder", new { id = o.Id }));
            builder.Link("note", o => LinkTarget.RouteTemplate("RnGetNote", new { id = o.Id }));
        }
    }

    private sealed class MissingTemplateLinks : LinkConfig<TemplateOrder>
    {
        public override void Configure(ILinkBuilder<TemplateOrder> builder)
        {
            builder.Self(o => LinkTarget.Route("RmGetOrder", new { id = o.Id }));
            builder.Link("search", _ => LinkTarget.RouteTemplate("NoSuchRoute"));
        }
    }
}
