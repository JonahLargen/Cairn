using Cairn.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

// AddCairnClient registration branches: the parameterless registration, and auto-redirect being
// disabled through a delegating wrapper and on a SocketsHttpHandler primary.
public class CairnClientDiBranchTests
{
    [Fact]
    public async Task AddCairnClient_without_configuration_registers_a_client()
    {
        var services = new ServiceCollection();
        services.AddCairnClient();

        await using var provider = services.BuildServiceProvider();

        // No options: no base address, default JSON, no link policy — the client still resolves.
        Assert.NotNull(provider.GetRequiredService<CairnClient>());
    }

    [Fact]
    public async Task Auto_redirect_is_disabled_through_a_delegating_wrapper_around_the_primary()
    {
        // Resilience/logging pipelines wrap the transport in delegating handlers; the unwrapping must
        // walk through them to reach the HttpClientHandler underneath.
        var transport = new HttpClientHandler { AllowAutoRedirect = true };
        var primary = new PassThroughHandler { InnerHandler = transport };

        var services = new ServiceCollection();
        services.AddCairnClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.AllowLink = _ => true;
        }).ConfigurePrimaryHttpMessageHandler(() => primary);

        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<CairnClient>();   // building the typed client builds the handler pipeline

        Assert.False(transport.AllowAutoRedirect);
    }

    [Fact]
    public async Task Auto_redirect_is_disabled_on_a_sockets_http_handler_primary()
    {
        var primary = new SocketsHttpHandler { AllowAutoRedirect = true };

        var services = new ServiceCollection();
        services.AddCairnClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.AllowLink = _ => true;
        }).ConfigurePrimaryHttpMessageHandler(() => primary);

        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<CairnClient>();

        Assert.False(primary.AllowAutoRedirect);
    }

    private sealed class PassThroughHandler : DelegatingHandler;
}
