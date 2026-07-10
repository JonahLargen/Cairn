using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnAlpsTests
{
    [Fact]
    public async Task The_index_lists_every_registered_profile_with_deterministic_names_and_hrefs()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/alps");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var profiles = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("profiles");

        // Sorted by full type name: AlpsCustomer before AlpsOrder.
        Assert.Equal(2, profiles.GetArrayLength());
        Assert.Equal("alps-customer", profiles[0].GetProperty("name").GetString());
        Assert.EndsWith("AlpsCustomer", profiles[0].GetProperty("resource").GetString(), StringComparison.Ordinal);
        Assert.Equal("/alps/alps-customer", profiles[0].GetProperty("href").GetString());
        Assert.Equal("alps-order", profiles[1].GetProperty("name").GetString());
        Assert.Equal("/alps/alps-order", profiles[1].GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_profile_serves_alps_json_with_the_resources_fields_as_semantic_descriptors()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/alps/alps-order");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/alps+json", response.Content.Headers.ContentType?.MediaType);

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("1.0", document.GetProperty("alps").GetProperty("version").GetString());

        // Fields carry the serializer's wire names (camelCase under the minimal-API defaults).
        Assert.Equal("semantic", Descriptor(document, "id").GetProperty("type").GetString());
        Assert.Equal("semantic", Descriptor(document, "status").GetProperty("type").GetString());

        // Cairn's injected hypermedia placeholders are not fields of the resource.
        Assert.DoesNotContain(Descriptors(document), d => d.TryGetProperty("id", out var id) && id.GetString() == "_links");
    }

    [Fact]
    public async Task Links_become_safe_descriptors_and_affordances_are_typed_by_their_method()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/alps-order")).RootElement;

        Assert.Equal("safe", Descriptor(document, "self").GetProperty("type").GetString());

        // The 'customer' relation collides with the resource's own 'customer' field, so the link's
        // descriptor moves to 'customer-link' (and the field keeps the plain id).
        Assert.Equal("semantic", Descriptor(document, "customer").GetProperty("type").GetString());
        var customer = Descriptor(document, "customer-link");
        Assert.Equal("customer", customer.GetProperty("name").GetString());
        Assert.Equal("safe", customer.GetProperty("type").GetString());
        Assert.Equal("The customer", customer.GetProperty("title").GetString());
        Assert.Equal("Deprecated: https://docs.example.com/gone", customer.GetProperty("doc").GetProperty("value").GetString());

        // POST -> unsafe, DELETE -> idempotent, GET -> safe; the When-gated cancel is still described.
        Assert.Equal("unsafe", Descriptor(document, "cancel").GetProperty("type").GetString());
        Assert.Equal("Cancel this order", Descriptor(document, "cancel").GetProperty("title").GetString());
        Assert.Equal("idempotent", Descriptor(document, "archive").GetProperty("type").GetString());
        Assert.Equal("safe", Descriptor(document, "refresh").GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_actions_input_fields_nest_as_semantics_and_reference_fields_the_document_already_declares()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/alps-order")).RootElement;
        var nested = Descriptor(document, "cancel").GetProperty("descriptor");

        Assert.Equal(2, nested.GetArrayLength());

        // 'reason' is new to the document: declared inline.
        Assert.Equal("reason", nested[0].GetProperty("id").GetString());
        Assert.Equal("semantic", nested[0].GetProperty("type").GetString());

        // 'status' is already declared as a resource field: referenced by fragment, not re-declared.
        Assert.Equal("#status", nested[1].GetProperty("href").GetString());
        Assert.False(nested[1].TryGetProperty("id", out _));
    }

    [Fact]
    public async Task A_relation_colliding_with_a_field_gets_a_suffixed_id_and_keeps_its_name()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/alps-order")).RootElement;

        // The 'status' link collides with the 'status' field, so the descriptor moves to 'status-link' and
        // carries the original relation as its name.
        var link = Descriptor(document, "status-link");
        Assert.Equal("safe", link.GetProperty("type").GetString());
        Assert.Equal("status", link.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_embed_is_a_semantic_descriptor_linking_to_the_childs_own_profile()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/alps-order")).RootElement;

        // The 'customer' embed collides with the 'customer' link, so it lands on 'customer-embedded'.
        var embed = Descriptor(document, "customer-embedded");
        Assert.Equal("semantic", embed.GetProperty("type").GetString());
        Assert.Equal("customer", embed.GetProperty("name").GetString());
        Assert.Contains("Embedded AlpsCustomer resource", embed.GetProperty("doc").GetProperty("value").GetString(), StringComparison.Ordinal);

        var link = embed.GetProperty("link")[0];
        Assert.Equal("profile", link.GetProperty("rel").GetString());
        Assert.Equal("/alps/alps-customer", link.GetProperty("href").GetString());
    }

    [Fact]
    public async Task An_unknown_profile_is_a_404()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/alps/nope");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_mount_path_and_profile_naming_are_configurable()
    {
        await using var app = await StartAsync(alps =>
        {
            alps.Path = "/meta/profiles";
            alps.ProfileName = type => type.Name.ToUpperInvariant();
        });
        using var client = app.GetTestClient();

        var index = JsonDocument.Parse(await client.GetStringAsync("/meta/profiles")).RootElement;
        Assert.Equal("/meta/profiles/ALPSCUSTOMER", index.GetProperty("profiles")[0].GetProperty("href").GetString());

        using var response = await client.GetAsync("/meta/profiles/ALPSORDER");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/alps+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Colliding_profile_names_get_deterministic_numeric_suffixes()
    {
        await using var app = await StartAsync(alps => alps.ProfileName = _ => "same");
        using var client = app.GetTestClient();

        var profiles = JsonDocument.Parse(await client.GetStringAsync("/alps")).RootElement.GetProperty("profiles");

        Assert.Equal("same", profiles[0].GetProperty("name").GetString());
        Assert.Equal("same-2", profiles[1].GetProperty("name").GetString());

        using var response = await client.GetAsync("/alps/same-2");
        response.EnsureSuccessStatusCode();
    }

    private static IEnumerable<JsonElement> Descriptors(JsonElement document)
        => document.GetProperty("alps").GetProperty("descriptor").EnumerateArray();

    private static JsonElement Descriptor(JsonElement document, string id)
        => Descriptors(document).Single(d => d.TryGetProperty("id", out var value) && value.GetString() == id);

    private static async Task<WebApplication> StartAsync(Action<CairnAlpsOptions>? configureAlps = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options =>
        {
            options.AddLinks(new AlpsCustomerLinks());
            options.AddLinks(new AlpsOrderLinks());
        });

        var app = builder.Build();
        app.MapCairnAlps(configureAlps);
        await app.StartAsync();
        return app;
    }

    private sealed record AlpsOrder(int Id, string Status, AlpsCustomer Customer);

    private sealed record AlpsCustomer(int Id);

    private sealed record CancelInput(string Reason, string Status);

    private sealed class AlpsOrderLinks : LinkConfig<AlpsOrder>
    {
        public override void Configure(ILinkBuilder<AlpsOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
            builder.Link("customer", o => LinkTarget.Uri($"/customers/{o.Customer.Id}"))
                .Title("The customer")
                .Deprecated("https://docs.example.com/gone");
            builder.Link("status", o => LinkTarget.Uri($"/orders/{o.Id}/status"));

            builder.Affordance("cancel", o => LinkTarget.Uri($"/orders/{o.Id}/cancel"))
                .Post()
                .Title("Cancel this order")
                .Accepts<CancelInput>()
                .When(o => o.Status == "Pending");
            builder.Affordance("archive", o => LinkTarget.Uri($"/orders/{o.Id}")).Delete();
            builder.Affordance("refresh", o => LinkTarget.Uri($"/orders/{o.Id}")).Get();

            builder.Embed("customer", o => o.Customer);
        }
    }

    private sealed class AlpsCustomerLinks : LinkConfig<AlpsCustomer>
    {
        public override void Configure(ILinkBuilder<AlpsCustomer> builder)
            => builder.Self(c => LinkTarget.Uri($"/customers/{c.Id}"));
    }
}
