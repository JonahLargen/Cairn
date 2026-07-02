using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

public class CairnEmitDiagnostic304Tests
{
    [Fact]
    public async Task A_healthy_conditional_get_answered_304_does_not_warn_or_meter_unemitted_hypermedia()
    {
        long unemitted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CairnDiagnostics.MeterName && instrument.Name == "cairn.hypermedia.unemitted")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "cairn.resource_type" && (string?)tag.Value == nameof(NotModifiedOrder))
                {
                    Interlocked.Add(ref unemitted, value);
                }
            }
        });
        listener.Start();

        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.AddLinks(new NotModifiedOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/nm/{id:int}", (int id) => TypedResults.Ok(new NotModifiedOrder(id, "v1")))
            .WithName("NmGetOrder")
            .WithETag((NotModifiedOrder o) => o.Version)
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        // Links are computed by WithLinks before WithETag short-circuits to 304 — a healthy conditional GET.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/nm/1");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(0, Interlocked.Read(ref unemitted));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("never emitted", StringComparison.Ordinal));
    }

    private sealed record NotModifiedOrder(int Id, string Version);

    private sealed class NotModifiedOrderLinks : LinkConfig<NotModifiedOrder>
    {
        public override void Configure(ILinkBuilder<NotModifiedOrder> builder)
            => builder.Self(o => LinkTarget.Route("NmGetOrder", new { id = o.Id }));
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
