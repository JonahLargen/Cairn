using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnMvcTests
{
    [Fact]
    public async Task Controller_action_with_CairnLinks_gets_links_and_affordances()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(MvcOrdersController).Assembly);
        builder.Services.AddCairn(o => o.AddLinks(new MvcOrderLinks()));

        await using var app = builder.Build();
        app.MapControllers();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetStringAsync("/mvc-orders/42");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.EndsWith("/mvc-orders/42", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("/mvc-orders/42/cancel", root.GetProperty("_actions").GetProperty("cancel").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Controller_action_without_CairnLinks_is_unchanged()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(MvcOrdersController).Assembly);
        builder.Services.AddCairn(o => o.AddLinks(new MvcOrderLinks()));

        await using var app = builder.Build();
        app.MapControllers();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetStringAsync("/mvc-orders/plain/42");
        using var document = JsonDocument.Parse(json);

        Assert.Equal(42, document.RootElement.GetProperty("id").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("_links", out _));
    }
}

[ApiController]
[Route("mvc-orders")]
public sealed class MvcOrdersController : ControllerBase
{
    [HttpGet("{id:int}", Name = "MvcGetOrder")]
    [CairnLinks]
    public MvcOrder Get(int id) => new(id);

    [HttpGet("plain/{id:int}")]
    public MvcOrder Plain(int id) => new(id);

    [HttpPost("{id:int}/cancel", Name = "MvcCancelOrder")]
    public IActionResult Cancel(int id) => NoContent();
}

public sealed record MvcOrder(int Id);

public sealed class MvcOrderLinks : LinkConfig<MvcOrder>
{
    public override void Configure(ILinkBuilder<MvcOrder> builder)
    {
        builder.Self(order => LinkTarget.Route("MvcGetOrder", new { id = order.Id }));
        builder.Affordance("cancel", order => LinkTarget.Route("MvcCancelOrder", new { id = order.Id })).Method("POST");
    }
}
