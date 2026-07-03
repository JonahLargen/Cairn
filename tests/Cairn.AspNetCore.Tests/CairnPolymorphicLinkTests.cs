using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Regression: a resource served through a declared base type — a List<Animal> of Dog items, or an
// Animal-typed single result — must still emit the links configured for its runtime subtype. The serializer
// builds and caches the contract for the declared type, so that base type's contract has to carry the
// injected hypermedia properties even though only the subtype has a LinkConfig. The emit-stage scoping added
// in #67 gated on the declared type alone and dropped these links.
public class CairnPolymorphicLinkTests
{
    [Fact]
    public async Task A_collection_of_a_configured_subtype_declared_as_its_base_emits_links()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var items = JsonDocument.Parse(await client.GetStringAsync("/animals")).RootElement.EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("/animals/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("/animals/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_single_configured_subtype_declared_as_its_base_emits_links()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/animal/7")).RootElement;

        Assert.Equal("/animals/7", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DogLinks()));

        var app = builder.Build();

        // The declared element/return type is the base Animal; only Dog (the runtime type) is configured.
        app.MapGet("/animals", () => TypedResults.Ok(new List<Animal> { new Dog(1), new Dog(2) })).WithLinks();
        app.MapGet("/animal/{id:int}", (int id) => TypedResults.Ok<Animal>(new Dog(id))).WithLinks();
        await app.StartAsync();
        return app;
    }

    private record Animal(int Id);

    private sealed record Dog(int Id) : Animal(Id);

    private sealed class DogLinks : LinkConfig<Dog>
    {
        public override void Configure(ILinkBuilder<Dog> builder)
            => builder.Self(dog => LinkTarget.Uri($"/animals/{dog.Id}"));
    }
}
