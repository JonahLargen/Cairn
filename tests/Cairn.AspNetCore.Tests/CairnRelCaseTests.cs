using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnRelCaseTests
{
    [Fact]
    public async Task Relations_differing_only_in_case_group_into_one_link_array()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new MixedCaseLinks()));

        await using var app = builder.Build();
        app.MapGet("/mc/{id:int}", (int id) => TypedResults.Ok(new MixedCaseOrder(id))).WithName("McGetOrder").WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/mc/7")).RootElement.GetProperty("_links");

        // "Related" and "related" are the same rel per RFC 8288 — one key (first declared casing), two links.
        var related = links.GetProperty("Related");
        Assert.Equal(JsonValueKind.Array, related.ValueKind);
        Assert.Equal(2, related.GetArrayLength());
        Assert.False(links.TryGetProperty("related", out _));
    }

    private sealed record MixedCaseOrder(int Id);

    private sealed class MixedCaseLinks : LinkConfig<MixedCaseOrder>
    {
        public override void Configure(ILinkBuilder<MixedCaseOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/mc/{o.Id}"));
            builder.Link("Related", o => LinkTarget.Uri($"/mc/{o.Id}/a"));
            builder.Link("related", o => LinkTarget.Uri($"/mc/{o.Id}/b"));
        }
    }
}
