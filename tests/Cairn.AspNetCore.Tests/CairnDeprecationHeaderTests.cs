using System.Collections.Concurrent;
using System.Globalization;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    public async Task WithDeprecation_without_arguments_emits_a_dated_deprecation_header()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/older", () => TypedResults.Ok(new { ok = true })).WithDeprecation();

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/older");

        // RFC 9745 defines no boolean form (that died with the draft); with no date supplied the header
        // falls back to the registration time as a structured-field Date.
        var deprecation = Assert.Single(response.Headers.GetValues("Deprecation"));
        Assert.StartsWith("@", deprecation);
        var seconds = long.Parse(deprecation[1..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.False(response.Headers.Contains("Sunset"));
        Assert.False(response.Headers.Contains("Link"));
    }

    [Fact]
    public async Task WithDeprecation_emits_all_headers_on_an_mvc_controller_endpoint()
    {
        var deprecatedAt = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var sunset = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(DeprecatedThingsController).Assembly);
        builder.Services.AddCairn();

        await using var app = builder.Build();

        // The endpoint-filter implementation compiled fine here but silently emitted nothing: endpoint
        // filters never run for controller actions. Metadata + middleware must reach them.
        app.MapControllers()
            .WithDeprecation(sunset: sunset, link: "https://docs.example.com/deprecations/things", deprecatedAt: deprecatedAt);

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/deprecated-things/7");
        response.EnsureSuccessStatusCode();

        var deprecation = Assert.Single(response.Headers.GetValues("Deprecation"));
        Assert.Equal($"@{deprecatedAt.ToUnixTimeSeconds()}", deprecation);

        var sunsetHeader = Assert.Single(response.Headers.GetValues("Sunset"));
        Assert.Equal(sunset.UtcDateTime.ToString("R", CultureInfo.InvariantCulture), sunsetHeader);

        Assert.Contains(response.Headers.GetValues("Link"), v => v.Contains("rel=\"deprecation\"", StringComparison.Ordinal)
            && v.Contains("https://docs.example.com/deprecations/things", StringComparison.Ordinal));
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

    [Fact]
    public async Task WithDeprecation_without_AddCairn_warns_once_that_the_headers_are_inert()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        // Deliberately no AddCairn: the metadata is attached, but nothing registers the emitting middleware.

        await using var app = builder.Build();
        app.MapGet("/old", () => TypedResults.Ok(new { ok = true })).WithDeprecation();
        app.MapGet("/older", () => TypedResults.Ok(new { ok = true })).WithDeprecation();

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/old");
        response.EnsureSuccessStatusCode();
        Assert.False(response.Headers.Contains("Deprecation"));

        // The silent no-op is surfaced — once per host, not once per deprecated endpoint.
        Assert.Single(logs.Messages, m => m.Contains("AddCairn was not called", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithDeprecation_with_AddCairn_does_not_warn()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/old", () => TypedResults.Ok(new { ok = true })).WithDeprecation();

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/old");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("Deprecation"));

        Assert.DoesNotContain(logs.Messages, m => m.Contains("AddCairn was not called", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }
}

[ApiController]
[Route("deprecated-things")]
public sealed class DeprecatedThingsController : ControllerBase
{
    [HttpGet("{id:int}")]
    public object Get(int id) => new { id };
}
