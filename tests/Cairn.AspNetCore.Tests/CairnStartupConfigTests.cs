using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

// Startup-time configuration diagnostics: the trusted-Host warning for absolute URLs, the freeze that makes
// too-late structural configuration fail loudly, and formatter media-type validation.
public class CairnStartupConfigTests
{
    [Fact]
    public async Task Absolute_urls_with_nothing_correcting_the_host_warn_at_startup()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn();   // UrlStyle defaults to Absolute; no PublicBaseUri, no forwarded headers

        await using var app = builder.Build();
        await app.StartAsync();

        await logs.WaitForAsync(m => m.Contains("UrlStyle is Absolute", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m =>
            m.Contains("UrlStyle is Absolute", StringComparison.Ordinal)
            && m.Contains("PublicBaseUri", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setting_PublicBaseUri_suppresses_the_host_warning()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.PublicBaseUri = new Uri("https://api.example.com"));

        await using var app = builder.Build();
        await app.StartAsync();

        Assert.DoesNotContain(logs.Messages, m => m.Contains("UrlStyle is Absolute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PathRelative_urls_suppress_the_host_warning()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddCairn(o => o.UrlStyle = LinkUrlStyle.PathRelative);

        await using var app = builder.Build();
        await app.StartAsync();

        Assert.DoesNotContain(logs.Messages, m => m.Contains("UrlStyle is Absolute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configured_forwarded_headers_suppress_the_host_warning()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
            o.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto);
        builder.Services.AddCairn();

        await using var app = builder.Build();
        await app.StartAsync();

        Assert.DoesNotContain(logs.Messages, m => m.Contains("UrlStyle is Absolute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Structural_configuration_after_first_resolution_fails_loudly()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        var options = app.Services.GetRequiredService<CairnOptions>();   // first resolution freezes

        // Each structural mutator would feed caches already built (or about to be) from the frozen state.
        Assert.Throws<InvalidOperationException>(() => options.AddLinks(new LateLinks()));
        Assert.Throws<InvalidOperationException>(() => options.AddLinksFromAssembly(typeof(CairnStartupConfigTests).Assembly));
        Assert.Throws<InvalidOperationException>(() => options.AddCurie("acme", "https://docs.example.com/{rel}"));
        Assert.Throws<InvalidOperationException>(() => options.AddFormatter(new StubFormatter("application/vnd.acme+json")));
        Assert.Throws<InvalidOperationException>(() => options.AddPaging<LateEnvelope>(e => new PagedView(e.Items, 1, 10, 0)));
        Assert.Throws<InvalidOperationException>(() => options.AddCursorPaging<LateEnvelope>(e => new CursorView(e.Items, null, null)));
    }

    [Fact]
    public void Configuration_before_resolution_is_unaffected_by_the_freeze()
    {
        var options = new CairnOptions();
        options.AddLinks(new LateLinks()).AddCurie("acme", "https://docs.example.com/{rel}");   // must not throw
    }

    [Theory]
    [InlineData("application/*")]
    [InlineData("*/*")]
    [InlineData("no-slash")]
    [InlineData("application/json; v=1")]
    public void AddFormatter_rejects_unusable_media_types(string mediaType)
    {
        var options = new CairnOptions();
        var failure = Assert.Throws<ArgumentException>(() => options.AddFormatter(new StubFormatter(mediaType)));
        Assert.Contains(mediaType, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFormatter_accepts_a_concrete_media_type()
    {
        new CairnOptions().AddFormatter(new StubFormatter("application/vnd.acme+json"));   // must not throw
    }

    private sealed record LateOrder(int Id);

    private sealed record LateEnvelope(List<LateOrder> Items);

    private sealed class LateLinks : LinkConfig<LateOrder>
    {
        public override void Configure(ILinkBuilder<LateOrder> builder)
            => builder.Self(o => LinkTarget.Uri($"/late/{o.Id}"));
    }

    private sealed class StubFormatter(string mediaType) : IHypermediaFormatter
    {
        public string MediaType => mediaType;

        public IReadOnlyList<HypermediaFormatProperty> Properties { get; } =
            [new HypermediaFormatProperty("links", _ => null)];
    }
}
