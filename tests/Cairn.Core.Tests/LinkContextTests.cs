namespace Cairn.Core.Tests;

public class LinkContextTests
{
    [Fact]
    public void An_omitted_service_provider_falls_back_to_an_empty_one()
    {
        var context = new LinkContext(new NullUrlResolver(), new AllowAllAuthorizer());

        // Service-aware conditions and targets can always resolve against Services; without a host-supplied
        // provider every lookup just returns null.
        Assert.NotNull(context.Services);
        Assert.Null(context.Services.GetService(typeof(string)));
    }

    [Fact]
    public void A_supplied_service_provider_is_exposed_as_is()
    {
        var services = new SingleServiceProvider("the-service");

        var context = new LinkContext(new NullUrlResolver(), new AllowAllAuthorizer(), services: services);

        Assert.Same(services, context.Services);
        Assert.Equal("the-service", context.Services.GetService(typeof(string)));
    }

    private sealed class SingleServiceProvider(string value) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(string) ? value : null;
    }

    private sealed class NullUrlResolver : ILinkUrlResolver
    {
        public string? Resolve(LinkTarget target) => null;
    }

    private sealed class AllowAllAuthorizer : ILinkAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(string policy, CancellationToken cancellationToken = default) => new(true);
    }
}
