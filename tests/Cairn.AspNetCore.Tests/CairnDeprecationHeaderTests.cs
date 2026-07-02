using System.Globalization;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnDeprecationHeaderTests
{
    [Fact]
    public async Task WithDeprecation_emits_deprecation_sunset_and_link_headers()
    {
        var deprecatedAt = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var sunset = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/old", () => TypedResults.Ok(new { ok = true }))
            .WithDeprecation(sunset: sunset, link: "https://docs.example.com/deprecations/old", deprecatedAt: deprecatedAt);

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/old");
        response.EnsureSuccessStatusCode();

        // RFC 9745: a structured-field Date (@ + seconds since the epoch).
        var deprecation = Assert.Single(response.Headers.GetValues("Deprecation"));
        Assert.Equal($"@{deprecatedAt.ToUnixTimeSeconds()}", deprecation);

        // RFC 8594: an HTTP-date.
        var sunsetHeader = Assert.Single(response.Headers.GetValues("Sunset"));
        Assert.Equal(sunset.UtcDateTime.ToString("R", CultureInfo.InvariantCulture), sunsetHeader);

        Assert.Contains(response.Headers.GetValues("Link"), v => v.Contains("rel=\"deprecation\"", StringComparison.Ordinal)
            && v.Contains("https://docs.example.com/deprecations/old", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithDeprecation_without_arguments_emits_only_the_boolean_deprecation_header()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/older", () => TypedResults.Ok(new { ok = true })).WithDeprecation();

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/older");

        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Deprecation")));
        Assert.False(response.Headers.Contains("Sunset"));
        Assert.False(response.Headers.Contains("Link"));
    }

    [Fact]
    public async Task WithDeprecation_composes_with_WithLinks()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        var sunset = DateTimeOffset.UtcNow.AddDays(30);
        app.MapGet("/dl", () => TypedResults.Ok(new { ok = true })).WithLinks().WithDeprecation(sunset: sunset);

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/dl");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("Deprecation"));
        Assert.True(response.Headers.Contains("Sunset"));
    }
}
