using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnPaginationEdgeTests
{
    [Fact]
    public async Task Single_page_has_self_first_last_but_no_prev_or_next()
    {
        var links = await PageLinksAsync(page: 1, size: 10, total: 5);   // TotalPages = 1

        Assert.True(links.TryGetProperty("self", out _));
        Assert.True(links.TryGetProperty("first", out _));
        Assert.True(links.TryGetProperty("last", out _));
        Assert.False(links.TryGetProperty("prev", out _));
        Assert.False(links.TryGetProperty("next", out _));
    }

    [Fact]
    public async Task Empty_result_has_only_self()
    {
        var links = await PageLinksAsync(page: 1, size: 10, total: 0);   // TotalPages = 0

        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(links.TryGetProperty("first", out _));
        Assert.False(links.TryGetProperty("last", out _));
        Assert.False(links.TryGetProperty("prev", out _));
        Assert.False(links.TryGetProperty("next", out _));
    }

    [Fact]
    public async Task Zero_page_size_does_not_throw_and_yields_only_self()
    {
        var links = await PageLinksAsync(page: 1, size: 0, total: 5);   // TotalPages = 0 (no divide-by-zero)

        Assert.True(links.TryGetProperty("self", out _));
        Assert.False(links.TryGetProperty("first", out _));
        Assert.False(links.TryGetProperty("next", out _));
    }

    private static async Task<JsonElement> PageLinksAsync(int page, int size, int total)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/p", (int page, int size, int total) =>
                TypedResults.Ok(new PagedResource<int>([], page, size, total)))
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetStringAsync($"/p?page={page}&size={size}&total={total}");
        return JsonDocument.Parse(json).RootElement.GetProperty("_links").Clone();
    }
}
