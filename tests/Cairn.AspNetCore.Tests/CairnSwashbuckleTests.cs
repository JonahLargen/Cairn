using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Swashbuckle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnSwashbuckleTests
{
    [Fact]
    public async Task Swagger_document_includes_links_and_actions_on_linked_types()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new SwaggerOrderLinks()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c => c.AddCairnHypermedia());

        await using var app = builder.Build();
        app.UseSwagger();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new SwaggerOrder(id)))
            .WithName("SwaggerOrderById")
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetStringAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(json);

        var properties = document.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("SwaggerOrder").GetProperty("properties");

        Assert.True(properties.TryGetProperty("_links", out _));
        Assert.True(properties.TryGetProperty("_actions", out _));
    }

    private sealed record SwaggerOrder(int Id);

    private sealed class SwaggerOrderLinks : LinkConfig<SwaggerOrder>
    {
        public override void Configure(ILinkBuilder<SwaggerOrder> builder)
            => builder.Self(order => LinkTarget.Route("SwaggerOrderById", new { id = order.Id }));
    }
}
