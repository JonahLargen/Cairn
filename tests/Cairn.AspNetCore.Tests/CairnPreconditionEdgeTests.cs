using System.Net;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnPreconditionEdgeTests
{
    [Fact]
    public async Task If_none_match_star_fails_with_412_when_the_resource_exists()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // The create-only PUT idiom: the request must not overwrite an existing resource.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/docs/5");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task If_none_match_star_passes_when_the_resource_does_not_exist()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/docs/404");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task If_none_match_with_a_matching_tag_fails_and_a_stale_tag_passes()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var matching = new HttpRequestMessage(HttpMethod.Put, "/docs/5");
        matching.Headers.TryAddWithoutValidation("If-None-Match", "\"v5\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(matching)).StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Put, "/docs/5");
        stale.Headers.TryAddWithoutValidation("If-None-Match", "\"v4\"");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task Require_if_match_is_satisfied_by_a_create_only_if_none_match()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // A guarded create carries If-None-Match: *, not If-Match — demanding 428 would break the idiom.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/guarded/404");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

        // Without any conditional header the lost-update protection still demands one.
        Assert.Equal((HttpStatusCode)428, (await client.PutAsync("/guarded/5", content: null)).StatusCode);
    }

    [Fact]
    public async Task If_match_fails_with_412_when_the_resource_does_not_exist()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // RFC 9110 §13.1.1: If-Match can only succeed against a current representation.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/docs/404");
        request.Headers.TryAddWithoutValidation("If-Match", "\"v404\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task WithETag_survives_selector_values_containing_quotes_and_non_ascii()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var first = await client.GetAsync("/hostile/1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = Assert.Single(first.Headers.GetValues("ETag"));

        // The escaped tag round-trips: a conditional GET with it still answers 304.
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/hostile/1");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        var app = builder.Build();

        // id 404 plays the "does not exist" case; anything else has ETag "v{id}".
        app.MapPut("/docs/{id:int}", (int id, HttpRequest request) =>
            CairnPreconditions.Evaluate(request, id == 404 ? null : $"v{id}")
                ?? (id == 404 ? Results.Created($"/docs/{id}", null) : Results.NoContent()));

        app.MapPut("/guarded/{id:int}", (int id, HttpRequest request) =>
            CairnPreconditions.Evaluate(request, id == 404 ? null : $"v{id}", requireIfMatch: true)
                ?? (id == 404 ? Results.Created($"/guarded/{id}", null) : Results.NoContent()));

        // A version selector an app might realistically produce: quotes and non-ASCII text.
        app.MapGet("/hostile/{id:int}", (int id) => TypedResults.Ok(new HostileDoc(id, $"ve\"r sioñ-{id}")))
            .WithETag((HostileDoc d) => d.Version);

        await app.StartAsync();
        return app;
    }

    private sealed record HostileDoc(int Id, string Version);
}
