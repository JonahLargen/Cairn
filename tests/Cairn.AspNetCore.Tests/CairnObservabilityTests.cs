using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

public class CairnObservabilityTests
{
    [Fact]
    public async Task A_lax_mode_drop_warns_once_and_increments_the_unresolved_counter()
    {
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CairnDiagnostics.MeterName && instrument.Name == "cairn.links.unresolved")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "cairn.resource_type" && (string?)tag.Value == nameof(ObsUnresolvedResource))
                {
                    Interlocked.Add(ref dropped, value);
                }
            }
        });
        listener.Start();

        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new ObsUnresolvedLinks()));

        await using var app = builder.Build();
        app.MapGet("/obs/{id:int}", (int id) => TypedResults.Ok(new ObsUnresolvedResource(id))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/obs/1");
        await client.GetStringAsync("/obs/2");

        Assert.DoesNotContain("_links", body);
        Assert.Equal(2, Interlocked.Read(ref dropped));   // metered on every drop
        Assert.Single(logs.Messages, m => m.Contains(nameof(ObsUnresolvedResource), StringComparison.Ordinal) && m.Contains("dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Computing_hypermedia_emits_an_activity_tagged_with_the_resource_type_and_format()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CairnDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new ObsLinkedLinks()));

        await using var app = builder.Build();
        app.MapGet("/obsa/{id:int}", (int id) => TypedResults.Ok(new ObsLinkedResource(id))).WithName("ObsActivitySelf").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();
        await client.GetStringAsync("/obsa/1");

        var activity = Assert.Single(activities, a => (string?)a.GetTagItem("cairn.resource_type") == nameof(ObsLinkedResource));
        Assert.Equal("Cairn.ComputeHypermedia", activity.OperationName);
        Assert.Equal("Default", activity.GetTagItem("cairn.format"));
    }

    [Fact]
    public async Task Computed_links_are_counted()
    {
        long resources = 0;
        long links = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CairnDiagnostics.MeterName
                && instrument.Name is "cairn.resources.linked" or "cairn.links.computed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "cairn.resources.linked")
            {
                Interlocked.Add(ref resources, value);
            }
            else
            {
                Interlocked.Add(ref links, value);
            }
        });
        listener.Start();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new ObsLinkedLinks()));

        await using var app = builder.Build();
        app.MapGet("/obsc/{id:int}", (int id) => TypedResults.Ok(new ObsLinkedResource(id))).WithName("ObsActivitySelf").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();
        await client.GetStringAsync("/obsc/1");

        // Other tests run in parallel against the same process-wide meter, so assert at-least rather than exact.
        Assert.True(Interlocked.Read(ref resources) >= 1);
        Assert.True(Interlocked.Read(ref links) >= 1);
    }

    private sealed record ObsUnresolvedResource(int Id);

    private sealed record ObsLinkedResource(int Id);

    private sealed class ObsUnresolvedLinks : LinkConfig<ObsUnresolvedResource>
    {
        public override void Configure(ILinkBuilder<ObsUnresolvedResource> builder)
            => builder.Self(r => LinkTarget.Route("ObsDoesNotExist", new { id = r.Id }));
    }

    private sealed class ObsLinkedLinks : LinkConfig<ObsLinkedResource>
    {
        public override void Configure(ILinkBuilder<ObsLinkedResource> builder)
            => builder.Self(r => LinkTarget.Route("ObsActivitySelf", new { id = r.Id }));
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
