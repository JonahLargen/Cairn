using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnCurieTests
{
    [Fact]
    public async Task A_curied_relation_surfaces_a_curies_array()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddLinks(new CurieOrderLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new CurieOrder(id))).WithName("CurieGetOrder").WithLinks();
        app.MapGet("/widgets/{id:int}", (int id) => TypedResults.Ok(new { id })).WithName("CurieGetWidget");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/orders/7")).RootElement.GetProperty("_links");

        // curies is always an array, even with a single entry.
        var curies = links.GetProperty("curies");
        Assert.Equal(JsonValueKind.Array, curies.ValueKind);
        Assert.Equal(1, curies.GetArrayLength());
        Assert.Equal("acme", curies[0].GetProperty("name").GetString());
        Assert.Equal("https://docs.example.com/rels/{rel}", curies[0].GetProperty("href").GetString());
        Assert.True(curies[0].GetProperty("templated").GetBoolean());

        // The curied relation is present; an uncuried relation does not add a curie.
        Assert.True(links.TryGetProperty("acme:widget", out _));
    }

    [Fact]
    public async Task No_curies_entry_when_no_relation_uses_a_registered_prefix()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddCurie("acme", "https://docs.example.com/rels/{rel}");
            o.AddLinks(new PlainOrderLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/p/{id:int}", (int id) => TypedResults.Ok(new CurieOrder(id))).WithName("PlainGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/p/7")).RootElement.GetProperty("_links");

        Assert.False(links.TryGetProperty("curies", out _));
    }

    private sealed record CurieOrder(int Id);

    private sealed class CurieOrderLinks : LinkConfig<CurieOrder>
    {
        public override void Configure(ILinkBuilder<CurieOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("CurieGetOrder", new { id = order.Id }));
            builder.Link("acme:widget", order => LinkTarget.Route("CurieGetWidget", new { id = order.Id }));
        }
    }

    private sealed class PlainOrderLinks : LinkConfig<CurieOrder>
    {
        public override void Configure(ILinkBuilder<CurieOrder> builder)
            => builder.Self(order => LinkTarget.Route("PlainGetOrder", new { id = order.Id }));
    }
}
