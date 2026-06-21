using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnClientDiTests
{
    [Fact]
    public async Task AddCairnClient_registers_a_working_typed_client()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DiOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new DiOrder(id))).WithName("DiGetOrder").WithLinks();
        await app.StartAsync();

        var services = new ServiceCollection();
        services.AddCairnClient(o => o.BaseAddress = new Uri("http://localhost"))
            .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<CairnClient>();

        var result = await client.GetAsync<DiOrder>("/orders/7");

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value!.Id);                         // ClientResult<T>.Value convenience
        Assert.Equal(7, result.Resource.RequireValue().Id);        // Resource<T>.RequireValue convenience
    }

    private sealed record DiOrder(int Id);

    private sealed class DiOrderLinks : LinkConfig<DiOrder>
    {
        public override void Configure(ILinkBuilder<DiOrder> builder)
            => builder.Self(order => LinkTarget.Route("DiGetOrder", new { id = order.Id }));
    }
}
