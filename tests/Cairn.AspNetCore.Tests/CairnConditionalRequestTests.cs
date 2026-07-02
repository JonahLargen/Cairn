using System.Net;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

public class CairnConditionalRequestTests
{
    [Fact]
    public async Task WithETag_emits_the_tag_and_answers_a_matching_conditional_get_with_304()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        var first = await client.GetAsync("/versioned/5");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag?.ToString();
        Assert.Equal("\"v5\"", etag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/versioned/5");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal("\"v5\"", second.Headers.ETag?.ToString());
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task WithETag_serves_the_body_when_the_tag_does_not_match()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/versioned/5");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"stale\"");
        var response = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"id\":5", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_already_quoted_or_weak_tag_is_passed_through_unchanged()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/weak/5");
        Assert.Equal("W/\"v5\"", response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Preconditions_pass_a_matching_if_match_and_fail_a_stale_one_with_412()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        using var fresh = new HttpRequestMessage(HttpMethod.Put, "/versioned/5");
        fresh.Headers.TryAddWithoutValidation("If-Match", "\"v5\"");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(fresh)).StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Put, "/versioned/5");
        stale.Headers.TryAddWithoutValidation("If-Match", "\"v4\"");
        var conflict = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Preconditions_can_require_if_match_with_428()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        var response = await client.PutAsync("/versioned/5", content: null);
        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task Options_answers_with_the_allowed_methods_of_the_matched_route()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        using var options = new HttpRequestMessage(HttpMethod.Options, "/versioned/5");
        var response = await client.SendAsync(options);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("GET, HEAD, PUT, OPTIONS", string.Join(", ", response.Content.Headers.Allow.Count > 0 ? response.Content.Headers.Allow : response.Headers.GetValues("Allow")));
    }

    [Fact]
    public async Task Options_falls_through_to_404_on_an_unknown_path()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        using var options = new HttpRequestMessage(HttpMethod.Options, "/nowhere");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(options)).StatusCode);
    }

    [Fact]
    public async Task An_app_mapped_options_endpoint_wins_over_the_middleware()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        using var options = new HttpRequestMessage(HttpMethod.Options, "/custom-options");
        var response = await client.SendAsync(options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("custom", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        var app = builder.Build();
        app.UseCairnOptionsHandler();

        app.MapGet("/versioned/{id:int}", (int id) => TypedResults.Ok(new Versioned(id, $"v{id}")))
            .WithETag((Versioned v) => v.Version);

        app.MapPut("/versioned/{id:int}", (int id, HttpRequest request) =>
            CairnPreconditions.Evaluate(request, $"v{id}", requireIfMatch: true) ?? Results.NoContent());

        app.MapGet("/weak/{id:int}", (int id) => TypedResults.Ok(new Versioned(id, $"v{id}")))
            .WithETag((Versioned v) => $"W/\"{v.Version}\"");

        app.MapMethods("/custom-options", ["OPTIONS"], () => Results.Text("custom"));

        await app.StartAsync();
        return app;
    }

    private sealed record Versioned(int Id, string Version);
}
