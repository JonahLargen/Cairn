using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public partial class CairnAlpsTests
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

        Assert.Equal(3, nested.GetArrayLength());

        // 'reason' is new to the document: declared inline.
        Assert.Equal("reason", nested[0].GetProperty("id").GetString());
        Assert.Equal("semantic", nested[0].GetProperty("type").GetString());

        // 'status' is already declared as a resource field: referenced by fragment, not re-declared.
        Assert.Equal("#status", nested[1].GetProperty("href").GetString());
        Assert.False(nested[1].TryGetProperty("id", out _));

        // 'self' is taken by a non-field descriptor (the self link), so the input field moves to a
        // suffixed id and keeps its wire name.
        Assert.Equal("self-input", nested[2].GetProperty("id").GetString());
        Assert.Equal("self", nested[2].GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_action_without_object_shaped_input_nests_no_descriptors()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/alps-order")).RootElement;

        // Accepts<int> has an int contract, but not an object one — and int has no fields to describe.
        Assert.False(Descriptor(document, "noop").TryGetProperty("descriptor", out _));
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

        // An affordance colliding with a field gets the action suffix.
        var action = Descriptor(document, "status-action");
        Assert.Equal("idempotent", action.GetProperty("type").GetString());
        Assert.Equal("status", action.GetProperty("name").GetString());

        // When the kind suffix is taken too ('note', 'note-link', and 'note-link-2' are all fields), the id
        // walks the numbered candidates until one is free.
        var note = Descriptor(document, "note-link-3");
        Assert.Equal("safe", note.GetProperty("type").GetString());
        Assert.Equal("note", note.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_links_declared_profile_uri_rides_along_as_a_profile_link()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/alps-order")).RootElement;

        var link = Descriptor(document, "invoice").GetProperty("link")[0];
        Assert.Equal("profile", link.GetProperty("rel").GetString());
        Assert.Equal("https://schemas.example.com/invoice", link.GetProperty("href").GetString());
    }

    [Fact]
    public async Task An_embed_of_an_unregistered_child_carries_its_doc_but_no_profile_link()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/alps-order")).RootElement;

        var tags = Descriptor(document, "tags");
        Assert.Equal("semantic", tags.GetProperty("type").GetString());
        Assert.False(tags.TryGetProperty("name", out _));   // no collision: the id is the relation itself
        Assert.Contains("Embedded collection of AlpsTag resources", tags.GetProperty("doc").GetProperty("value").GetString(), StringComparison.Ordinal);
        Assert.False(tags.TryGetProperty("link", out _));   // AlpsTag has no registered config, so no profile to point at
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

    [Fact]
    public async Task Default_names_kebab_case_acronyms_digits_underscores_and_generics()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options =>
        {
            options.AddLinks(new NoLinks<HALDocument>());
            options.AddLinks(new NoLinks<OrderDTO>());
            options.AddLinks(new NoLinks<Grid2Cell>());
            options.AddLinks(new NoLinks<My_Tag>());
            options.AddLinks(new NoLinks<Box<Inner>>());
            options.AddLinks(new IntListLinks());
        });

        await using var app = builder.Build();
        app.MapCairnAlps();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var profiles = JsonDocument.Parse(await client.GetStringAsync("/alps")).RootElement.GetProperty("profiles");
        var names = profiles.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();

        Assert.Contains("hal-document", names);   // acronym run before a word
        Assert.Contains("order-dto", names);      // trailing acronym run
        Assert.Contains("grid2-cell", names);     // digit before an upper starts a word
        Assert.Contains("my_tag", names);         // underscores pass through and start no word
        Assert.Contains("box-inner", names);      // generic arity stripped, type arguments appended
        Assert.Contains("int-list", names);
    }

    [Fact]
    public async Task A_resource_without_an_object_contract_carries_no_field_descriptors()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options => options.AddLinks(new IntListLinks()));

        await using var app = builder.Build();
        app.MapCairnAlps();
        await app.StartAsync();
        using var client = app.GetTestClient();

        // A List<int>-derived resource serializes as an array — there are no fields to describe, but the
        // declared links are still there.
        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/int-list")).RootElement;
        var descriptor = Assert.Single(Descriptors(document));
        Assert.Equal("self", descriptor.GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_request_path_base_is_carried_into_index_and_cross_profile_hrefs()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options =>
        {
            options.AddLinks(new AlpsCustomerLinks());
            options.AddLinks(new AlpsOrderLinks());
        });

        await using var app = builder.Build();
        app.UsePathBase("/base");
        app.UseRouting();
        app.MapCairnAlps();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var index = JsonDocument.Parse(await client.GetStringAsync("/base/alps")).RootElement;
        Assert.Equal("/base/alps/alps-customer", index.GetProperty("profiles")[0].GetProperty("href").GetString());

        var document = JsonDocument.Parse(await client.GetStringAsync("/base/alps/alps-order")).RootElement;
        var embed = Descriptor(document, "customer-embedded");
        Assert.Equal("/base/alps/alps-customer", embed.GetProperty("link")[0].GetProperty("href").GetString());

        // The same host answers un-prefixed requests too; those documents keep un-prefixed hrefs.
        var bare = JsonDocument.Parse(await client.GetStringAsync("/alps")).RootElement;
        Assert.Equal("/alps/alps-customer", bare.GetProperty("profiles")[0].GetProperty("href").GetString());
    }

    [Fact]
    public async Task Mapping_without_AddCairn_fails_on_first_request_with_guidance()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        await using var app = builder.Build();
        app.MapCairnAlps();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/alps"));
        Assert.Contains("AddCairn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_profile_name_from_the_callback_fails_loudly()
    {
        await using var app = await StartAsync(alps => alps.ProfileName = _ => "  ");
        using var client = app.GetTestClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/alps"));
        Assert.Contains("non-empty profile name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_path_option_rejects_relative_and_empty_values_and_trims_a_trailing_slash()
    {
        var options = new CairnAlpsOptions();

        Assert.Throws<ArgumentException>(() => options.Path = "alps");
        Assert.Throws<ArgumentException>(() => options.Path = "/");
        Assert.Throws<ArgumentException>(() => options.Path = "   ");

        options.Path = "/meta/profiles/";
        Assert.Equal("/meta/profiles", options.Path);
    }

    [Fact]
    public async Task Under_a_source_gen_only_resolver_input_fields_fall_back_to_reflection_wire_names()
    {
        await using var app = await StartSourceGenOnlyAsync(camelCase: true);
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/fallback-resource")).RootElement;

        // The resolver has no contract for the resource type, so the profile carries no field descriptors...
        Assert.DoesNotContain(Descriptors(document), d => d.TryGetProperty("id", out var id) && id.GetString() == "id");

        // ...but the annotated input type still describes its fields: [JsonPropertyName] verbatim, the
        // options' naming policy for the rest.
        var nested = Descriptor(document, "submit").GetProperty("descriptor");
        Assert.Equal("why", nested[0].GetProperty("id").GetString());
        Assert.Equal("other", nested[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_reflection_fallback_uses_the_declared_name_when_there_is_no_naming_policy()
    {
        await using var app = await StartSourceGenOnlyAsync(camelCase: false);
        using var client = app.GetTestClient();

        var document = JsonDocument.Parse(await client.GetStringAsync("/alps/fallback-resource")).RootElement;

        var nested = Descriptor(document, "submit").GetProperty("descriptor");
        Assert.Equal("why", nested[0].GetProperty("id").GetString());
        Assert.Equal("Other", nested[1].GetProperty("id").GetString());
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

    private sealed record AlpsOrder(int Id, string Status, AlpsCustomer Customer)
    {
        // Occupy the 'note' relation's id and its first two collision candidates, so the 'note' link has to
        // walk all the way to the numbered suffixes.
        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("note-link")]
        public string? NoteLink { get; init; }

        [JsonPropertyName("note-link-2")]
        public string? NoteLink2 { get; init; }
    }

    private sealed record AlpsCustomer(int Id);

    private sealed record AlpsTag(string Value);

    private sealed record CancelInput(string Reason, string Status, string Self);

    private sealed class AlpsOrderLinks : LinkConfig<AlpsOrder>
    {
        public override void Configure(ILinkBuilder<AlpsOrder> builder)
        {
            builder.Self(o => LinkTarget.Uri($"/orders/{o.Id}"));
            builder.Link("customer", o => LinkTarget.Uri($"/customers/{o.Customer.Id}"))
                .Title("The customer")
                .Deprecated("https://docs.example.com/gone");
            builder.Link("status", o => LinkTarget.Uri($"/orders/{o.Id}/status"));
            builder.Link("note", o => LinkTarget.Uri($"/orders/{o.Id}/note"));
            builder.Link("invoice", o => LinkTarget.Uri($"/orders/{o.Id}/invoice"))
                .Profile("https://schemas.example.com/invoice");

            builder.Affordance("cancel", o => LinkTarget.Uri($"/orders/{o.Id}/cancel"))
                .Post()
                .Title("Cancel this order")
                .Accepts<CancelInput>()
                .When(o => o.Status == "Pending");
            builder.Affordance("archive", o => LinkTarget.Uri($"/orders/{o.Id}")).Delete();
            builder.Affordance("refresh", o => LinkTarget.Uri($"/orders/{o.Id}")).Get();
            builder.Affordance("status", o => LinkTarget.Uri($"/orders/{o.Id}/status")).Put();
            builder.Affordance("noop", o => LinkTarget.Uri($"/orders/{o.Id}/noop")).Accepts<int>();

            builder.Embed("customer", o => o.Customer);
            builder.EmbedMany("tags", _ => Array.Empty<AlpsTag>());
        }
    }

    private sealed class AlpsCustomerLinks : LinkConfig<AlpsCustomer>
    {
        public override void Configure(ILinkBuilder<AlpsCustomer> builder)
            => builder.Self(c => LinkTarget.Uri($"/customers/{c.Id}"));
    }

    // --- default-naming fixtures ---

    private sealed record HALDocument(int Id);

    private sealed record OrderDTO(int Id);

    private sealed record Grid2Cell(int Id);

    private sealed record My_Tag(int Id);

    private sealed record Box<T>(T Value);

    private sealed record Inner(int Id);

    private sealed class IntList : List<int>;

    private sealed class NoLinks<T> : LinkConfig<T>
    {
        public override void Configure(ILinkBuilder<T> builder)
        {
        }
    }

    private sealed class IntListLinks : LinkConfig<IntList>
    {
        public override void Configure(ILinkBuilder<IntList> builder)
            => builder.Self(_ => LinkTarget.Uri("/ints"));
    }

    // --- source-gen-only resolver fixtures ---

    private static async Task<WebApplication> StartSourceGenOnlyAsync(bool camelCase)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // A resolver that knows neither the resource nor the input type — the shape of a source-gen-only
        // host whose JsonSerializerContext doesn't cover a Cairn-configured type.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolver = LimitedJsonContext.Default;
            if (!camelCase)
            {
                options.SerializerOptions.PropertyNamingPolicy = null;
            }
        });
        builder.Services.AddCairn(options => options.AddLinks(new FallbackLinks()));

        var app = builder.Build();
        app.MapCairnAlps();
        await app.StartAsync();
        return app;
    }

    private sealed record FallbackResource(int Id);

    private sealed record FallbackInput([property: JsonPropertyName("why")] string Reason, string Other);

    private sealed class FallbackLinks : LinkConfig<FallbackResource>
    {
        public override void Configure(ILinkBuilder<FallbackResource> builder)
        {
            builder.Self(r => LinkTarget.Uri($"/fallback/{r.Id}"));
            builder.Affordance("submit", r => LinkTarget.Uri($"/fallback/{r.Id}/submit")).Accepts<FallbackInput>();
        }
    }

    [JsonSerializable(typeof(int))]
    private sealed partial class LimitedJsonContext : JsonSerializerContext;
}
