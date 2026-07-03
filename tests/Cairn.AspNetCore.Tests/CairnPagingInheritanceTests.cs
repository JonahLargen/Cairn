using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// AddPaging/AddCursorPaging lookups honor inheritance like LinkConfigRegistry: a subclass of a registered
// envelope type is decorated with the same pagination links.
public class CairnPagingInheritanceTests
{
    [Fact]
    public async Task A_subclass_of_a_registered_paged_envelope_gets_pagination_links()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/inh/paged?page=2")).RootElement;

        var links = root.GetProperty("_links");
        Assert.True(links.TryGetProperty("self", out _));
        Assert.True(links.TryGetProperty("next", out _));
        Assert.True(links.TryGetProperty("prev", out _));
    }

    [Fact]
    public async Task A_subclass_of_a_registered_cursor_envelope_gets_cursor_links()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/inh/cursor")).RootElement;

        Assert.True(root.GetProperty("_links").TryGetProperty("next", out _));
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o
            .AddPaging<InhBasePage>(p => new PagedView(p.Records, p.PageNo, p.Size, p.Total))
            .AddCursorPaging<InhBaseFeed>(f => new CursorView(f.Entries, f.After, null)));

        var app = builder.Build();
        app.MapGet("/inh/paged", (int page) => TypedResults.Ok(new InhSpecialPage
        {
            Records = [new InhItem(1)],
            PageNo = page,
            Size = 1,
            Total = 5,
        })).WithLinks();
        app.MapGet("/inh/cursor", () => TypedResults.Ok(new InhSpecialFeed { Entries = [new InhItem(1)], After = "a" })).WithLinks();
        await app.StartAsync();
        return app;
    }

    private sealed record InhItem(int Id);

    private class InhBasePage
    {
        public List<InhItem> Records { get; set; } = [];

        public int PageNo { get; set; }

        public int Size { get; set; }

        public int Total { get; set; }
    }

    // Only the base type is registered; the endpoint returns this subclass.
    private sealed class InhSpecialPage : InhBasePage
    {
        public string Flavor { get; set; } = "special";
    }

    private class InhBaseFeed
    {
        public List<InhItem> Entries { get; set; } = [];

        public string? After { get; set; }
    }

    private sealed class InhSpecialFeed : InhBaseFeed
    {
    }
}
