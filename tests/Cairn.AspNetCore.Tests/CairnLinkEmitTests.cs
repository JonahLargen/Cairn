using System.Net;
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnLinkEmitTests
{
    [Fact]
    public async Task A_templated_uri_link_serializes_with_templated_true()
    {
        await using var app = await StartAsync(b =>
            b.Link("search", _ => LinkTarget.Uri("/orders{?status,page}", templated: true)));
        using var client = app.GetTestClient();

        var search = (await GetJsonAsync(client, "/x/1")).GetProperty("_links").GetProperty("search");

        Assert.True(search.GetProperty("templated").GetBoolean());
        Assert.Equal("/orders{?status,page}", search.GetProperty("href").GetString());
    }

    [Fact]
    public async Task Lax_mode_drops_a_link_whose_explicit_href_is_empty_rather_than_500()
    {
        await using var app = await StartAsync(b =>
        {
            b.Self(o => LinkTarget.Route("EmitSelf", new { id = o.Id }));
            b.Link("maybe", _ => LinkTarget.Uri(""));
        });
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/x/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var links = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("_links");
        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(links.TryGetProperty("maybe", out _));
    }

    [Fact]
    public async Task A_link_type_hint_serializes_and_is_read_by_the_client()
    {
        await using var app = await StartAsync(b =>
            b.Self(o => LinkTarget.Route("EmitSelf", new { id = o.Id })).Type("application/pdf"));
        using var httpClient = app.GetTestClient();

        var resource = (await new CairnClient(httpClient).GetAsync<EmitResource>("/x/1")).EnsureSuccess();

        Assert.Equal("application/pdf", resource.Links["self"].Type);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
        => JsonDocument.Parse(await client.GetStringAsync(path)).RootElement.Clone();

    private static async Task<WebApplication> StartAsync(Action<ILinkBuilder<EmitResource>> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new EmitLinks(configure)));

        var app = builder.Build();
        app.MapGet("/x/{id:int}", (int id) => TypedResults.Ok(new EmitResource(id))).WithName("EmitSelf").WithLinks();
        await app.StartAsync();
        return app;
    }

    private sealed record EmitResource(int Id);

    private sealed class EmitLinks(Action<ILinkBuilder<EmitResource>> configure) : LinkConfig<EmitResource>
    {
        public override void Configure(ILinkBuilder<EmitResource> builder) => configure(builder);
    }
}
