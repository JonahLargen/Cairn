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

public class CairnWarnOnceIsolationTests
{
    [Fact]
    public async Task Each_host_gets_its_own_warn_once_diagnostics_for_the_same_type()
    {
        // The warn-once gate is per host container (an AddCairn singleton), not process-wide: a second host
        // in the same process — a WebApplicationFactory suite, side-by-side hosts — must not lose the
        // diagnostic because another host already warned about the same DTO type.
        var firstLogs = await WarnAsync();
        var secondLogs = await WarnAsync();

        Assert.Single(firstLogs.Messages, m => m.Contains("no link configuration", StringComparison.Ordinal) && m.Contains(nameof(SharedUnconfigured), StringComparison.Ordinal));
        Assert.Single(secondLogs.Messages, m => m.Contains("no link configuration", StringComparison.Ordinal) && m.Contains(nameof(SharedUnconfigured), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Within_one_host_the_diagnostic_still_fires_only_once()
    {
        var logs = await WarnAsync(requests: 3);

        Assert.Single(logs.Messages, m => m.Contains("no link configuration", StringComparison.Ordinal) && m.Contains(nameof(SharedUnconfigured), StringComparison.Ordinal));
    }

    // Starts a host whose endpoint opts into links but returns a type with no config — the warn-once
    // "unconfigured" diagnostic — and returns the captured logs.
    private static async Task<CapturingLoggerProvider> WarnAsync(int requests = 1)
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/shared/{id:int}", (int id) => TypedResults.Ok(new SharedUnconfigured(id))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();
        for (var i = 0; i < requests; i++)
        {
            await client.GetStringAsync($"/shared/{i}");
        }

        return logs;
    }

    private sealed record SharedUnconfigured(int Id);

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
