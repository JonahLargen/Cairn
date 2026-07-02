using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnPaginationQueryCaseTests
{
    [Fact]
    public async Task Page_parameter_swap_matches_case_insensitively_and_preserves_the_incoming_casing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/qc", () => new PagedResource<QcItem>([new(1)], Page: 2, PageSize: 5, TotalCount: 25)).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        // The client sent "Page" (query keys bind case-insensitively in ASP.NET Core); the rewritten URLs must
        // replace that parameter — not append a second "page" — and keep the incoming casing.
        var links = JsonDocument.Parse(await client.GetStringAsync("/qc?Page=2&size=5")).RootElement.GetProperty("_links");

        var next = links.GetProperty("next").GetProperty("href").GetString()!;
        Assert.Contains("Page=3", next, StringComparison.Ordinal);
        Assert.DoesNotContain("page=", next, StringComparison.Ordinal);
        Assert.Contains("size=5", next, StringComparison.Ordinal);

        var prev = links.GetProperty("prev").GetProperty("href").GetString()!;
        Assert.Contains("Page=1", prev, StringComparison.Ordinal);
        Assert.DoesNotContain("page=", prev, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cursor_parameter_swap_matches_case_insensitively_and_preserves_the_incoming_casing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/qcc", () => new CursorPage<QcItem>([new(1)], Next: "n2")).WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/qcc?CURSOR=n1")).RootElement.GetProperty("_links");

        var next = links.GetProperty("next").GetProperty("href").GetString()!;
        Assert.Contains("CURSOR=n2", next, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor=", next, StringComparison.Ordinal);
    }

    private sealed record QcItem(int Id);
}
