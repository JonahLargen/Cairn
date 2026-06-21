using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnLinkAttributeTests
{
    [Fact]
    public async Task Link_attributes_serialize_and_round_trip_through_the_client()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new AttrOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new AttrOrder(id))).WithName("AttrGetOrder").WithLinks();
        app.MapGet("/legacy/{id:int}", (int id) => TypedResults.Ok(new { id })).WithName("AttrLegacy");

        await app.StartAsync();
        using var httpClient = app.GetTestClient();

        var order = (await new CairnClient(httpClient).GetAsync<AttrOrder>("/orders/7")).EnsureSuccess();

        var legacy = order.Links["legacy"];
        Assert.Equal("v1", legacy.Name);
        Assert.Equal("https://docs.example.com/deprecations/legacy", legacy.Deprecation);
        Assert.Equal("en", legacy.Hreflang);
        Assert.Equal("https://schemas.example.com/order", legacy.Profile);
    }

    private sealed record AttrOrder(int Id);

    private sealed class AttrOrderLinks : LinkConfig<AttrOrder>
    {
        public override void Configure(ILinkBuilder<AttrOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("AttrGetOrder", new { id = order.Id }));
            builder.Link("legacy", order => LinkTarget.Route("AttrLegacy", new { id = order.Id }))
                .Name("v1")
                .Deprecated("https://docs.example.com/deprecations/legacy")
                .Hreflang("en")
                .Profile("https://schemas.example.com/order");
        }
    }
}
