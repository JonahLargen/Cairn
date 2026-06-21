using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnAssemblyScanTests
{
    [Fact]
    public async Task AddLinksFromAssembly_discovers_and_registers_configs()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinksFromAssemblyContaining<ScanOrder>());

        await using var app = builder.Build();
        app.MapGet("/scan/{id:int}", (int id) => TypedResults.Ok(new ScanOrder(id))).WithName("ScanGetOrder").WithLinks();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/scan/9")).RootElement;

        // ScanOrderLinks was discovered by scanning — no explicit AddLinks call.
        Assert.EndsWith("/scan/9", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    private sealed record ScanOrder(int Id);

    // A discoverable config: non-abstract, public parameterless constructor.
    private sealed class ScanOrderLinks : LinkConfig<ScanOrder>
    {
        public override void Configure(ILinkBuilder<ScanOrder> builder)
            => builder.Self(order => LinkTarget.Route("ScanGetOrder", new { id = order.Id }));
    }
}
