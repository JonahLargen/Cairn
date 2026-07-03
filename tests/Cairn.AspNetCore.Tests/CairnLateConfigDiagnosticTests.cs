using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

// When a link config is registered only after a type's JSON contract has already been built and cached, the
// contract carries no injected hypermedia properties and the computed links have nowhere to be emitted. The
// emit-miss diagnostic must name that cause (the contract predates the config) instead of blaming a deferred
// sequence.
public class CairnLateConfigDiagnosticTests
{
    [Fact]
    public async Task A_config_registered_after_the_contract_is_built_gets_a_scoping_specific_diagnostic()
    {
        var logs = new CapturingLoggerProvider();
        var provider = new TogglingConfigProvider();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);

        // Registering our provider before AddCairn means its TryAddSingleton keeps ours in place.
        builder.Services.AddSingleton<ILinkConfigProvider>(provider);
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/late", () => TypedResults.Ok(new LateOrder(1))).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        // Warm the contract cache while unconfigured: LateOrder's contract is built and cached with no
        // injected hypermedia properties.
        Assert.DoesNotContain("_links", await client.GetStringAsync("/late"), StringComparison.Ordinal);

        // The config "arrives" after the fact. Links now compute, but the cached contract cannot emit them.
        provider.Enable();
        Assert.DoesNotContain("_links", await client.GetStringAsync("/late"), StringComparison.Ordinal);

        await logs.WaitForAsync(m =>
            m.Contains("never emitted", StringComparison.Ordinal)
            && m.Contains("before a link config", StringComparison.Ordinal));

        Assert.Contains(logs.Messages, m =>
            m.Contains(nameof(LateOrder), StringComparison.Ordinal)
            && m.Contains("before a link config", StringComparison.Ordinal));

        // The old, misleading cause must not be logged for this case.
        Assert.DoesNotContain(logs.Messages, m => m.Contains("deferred sequence (LINQ projection", StringComparison.Ordinal));
    }

    private sealed record LateOrder(int Id);

    // Returns no config until Enable() is called, standing in for a config registered after the type's
    // serializer contract was first built and cached.
    private sealed class TogglingConfigProvider : ILinkConfigProvider
    {
        private volatile bool _enabled;

        public void Enable() => _enabled = true;

        public ICompiledLinkConfig? GetConfig(Type resourceType)
            => _enabled && resourceType == typeof(LateOrder) ? LateOrderConfig.Instance : null;
    }

    private sealed class LateOrderConfig : ICompiledLinkConfig
    {
        public static readonly LateOrderConfig Instance = new();

        public ValueTask<LinkSet> BuildAsync(object resource, LinkContext context, CancellationToken cancellationToken = default)
        {
            var order = (LateOrder)resource;
            return new ValueTask<LinkSet>(new LinkSet([new Link("self", $"/late/{order.Id}")], []));
        }
    }
}
