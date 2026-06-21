using System.Collections.Concurrent;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

public class CairnDiagnosticTests
{
    [Fact]
    public async Task Strict_mode_surfaces_a_link_resolution_exception_when_a_route_link_cannot_resolve()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.Mode = LinkResolutionMode.Strict;
            o.AddLinks(new UnresolvableLinks());
        });

        await using var app = builder.Build();
        // No endpoint is named "DoesNotExist", so the self link cannot resolve.
        app.MapGet("/x/{id:int}", (int id) => TypedResults.Ok(new DiagResource(id))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        // The recorder runs before serialization, so strict mode throws cleanly (a real host renders this as a
        // 500 with no half-written hypermedia); TestServer surfaces the exception to the caller.
        await Assert.ThrowsAsync<LinkResolutionException>(() => client.GetAsync("/x/1"));
    }

    [Fact]
    public async Task A_value_type_resource_yields_no_links_and_warns_once()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new StructLinks()));

        await using var app = builder.Build();
        app.MapGet("/s/{id:int}", (int id) => TypedResults.Ok(new StructResource(id))).WithName("StructSelf").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/s/1");
        await client.GetStringAsync("/s/2");   // second request must not warn again

        Assert.DoesNotContain("_links", body);
        Assert.Single(logs.Messages, m => m.Contains("value type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hal_mode_omits_actions_and_warns_once()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.Hal;
            o.AddLinks(new ActionLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/a/{id:int}", (int id) => TypedResults.Ok(new DiagResource(id))).WithName("ActionSelf").WithLinks();
        app.MapPost("/a/{id:int}/go", (int id) => TypedResults.NoContent()).WithName("ActionGo");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/a/1");
        await client.GetStringAsync("/a/2");

        Assert.DoesNotContain("_actions", body);
        Assert.Single(logs.Messages, m => m.Contains("HAL", StringComparison.Ordinal) && m.Contains("actions", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record DiagResource(int Id);

    private readonly record struct StructResource(int Id);

    private sealed class UnresolvableLinks : LinkConfig<DiagResource>
    {
        public override void Configure(ILinkBuilder<DiagResource> builder)
            => builder.Self(r => LinkTarget.Route("DoesNotExist", new { id = r.Id }));
    }

    private sealed class StructLinks : LinkConfig<StructResource>
    {
        public override void Configure(ILinkBuilder<StructResource> builder)
            => builder.Self(r => LinkTarget.Route("StructSelf", new { id = r.Id }));
    }

    private sealed class ActionLinks : LinkConfig<DiagResource>
    {
        public override void Configure(ILinkBuilder<DiagResource> builder)
        {
            builder.Self(r => LinkTarget.Route("ActionSelf", new { id = r.Id }));
            builder.Affordance("go", r => LinkTarget.Route("ActionGo", new { id = r.Id })).Method("POST");
        }
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
