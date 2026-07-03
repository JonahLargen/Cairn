using System.Net;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// A CORS preflight is an OPTIONS request too — the handler must leave it to the CORS middleware, which
// answers with the Access-Control-* headers browsers require.
public class CairnOptionsHandlerCorsTests
{
    [Fact]
    public async Task A_cors_preflight_is_answered_by_the_cors_middleware_not_hijacked()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/things/5");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://spa.example.com");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "PUT");
        var response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("Access-Control-Allow-Origin", response.Headers.Select(h => h.Key));
        Assert.Contains("Access-Control-Allow-Methods", response.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task A_plain_options_request_is_still_answered_with_allow()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var options = new HttpRequestMessage(HttpMethod.Options, "/things/5");
        var response = await client.SendAsync(options);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("GET, HEAD, PUT, OPTIONS", string.Join(", ", response.Content.Headers.Allow.Count > 0 ? response.Content.Headers.Allow : response.Headers.GetValues("Allow")));
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        var app = builder.Build();

        // The Cairn handler runs first — before the fix it would have answered the preflight itself.
        app.UseCairnOptionsHandler();
        app.UseCors();

        app.MapGet("/things/{id:int}", (int id) => TypedResults.Ok(new { id }));
        app.MapPut("/things/{id:int}", (int id) => TypedResults.NoContent());
        await app.StartAsync();
        return app;
    }
}
