using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnProblemDuplicateRelTests
{
    [Fact]
    public async Task Duplicate_rels_on_a_problem_emit_a_link_array_like_the_main_formatter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/pd", () => CairnResults.Problem(503, title: "Down")
            .WithLink("self", "/pd")
            .WithLink("help", "https://support.example.com/a", "Support A")
            .WithLink("help", "https://support.example.com/b", "Support B"));

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/pd");
        Assert.Equal(503, (int)response.StatusCode);

        var links = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("_links");

        // A single rel stays an object; a repeated rel becomes an array in declaration order.
        Assert.Equal(JsonValueKind.Object, links.GetProperty("self").ValueKind);
        var help = links.GetProperty("help");
        Assert.Equal(JsonValueKind.Array, help.ValueKind);
        Assert.Equal(2, help.GetArrayLength());
        Assert.Equal("https://support.example.com/a", help[0].GetProperty("href").GetString());
        Assert.Equal("Support A", help[0].GetProperty("title").GetString());
        Assert.Equal("https://support.example.com/b", help[1].GetProperty("href").GetString());
    }

    [Fact]
    public async Task Rels_differing_only_in_case_group_into_one_problem_link_array()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/pc", () => CairnResults.Problem(500)
            .WithLink("Help", "https://support.example.com/a")
            .WithLink("help", "https://support.example.com/b"));

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/pc");
        var links = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("_links");

        var help = links.GetProperty("Help");   // first declared casing wins
        Assert.Equal(JsonValueKind.Array, help.ValueKind);
        Assert.Equal(2, help.GetArrayLength());
    }
}
