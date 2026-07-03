using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// A pseudo resource class for [Display(ResourceType = ...)]: DisplayAttribute resolves the prompt through
// this static property on every call, so it must be public and top-level.
public static class ContractPrompts
{
    public static string StatusPrompt
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "Statut" : "Status";
}

// HAL-FORMS field names and option values must follow the app's JSON contract — a generic client builds its
// payload from the template, and the endpoint binds it through the serializer.
public class CairnHalFormsContractTests
{
    [Fact]
    public async Task Field_names_follow_the_hosts_property_naming_policy_and_JsonPropertyName()
    {
        await using var app = await StartAsync(services => services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower));
        using var client = app.GetTestClient();

        var names = await FieldNamesAsync(client);

        // snake_case shop: the endpoint binds order_note, not orderNote — the form must say so.
        Assert.Contains("order_note", names);
        Assert.DoesNotContain("orderNote", names);

        // [JsonPropertyName] beats any naming policy, exactly like it does when binding.
        Assert.Contains("renamed", names);
        Assert.DoesNotContain("custom_field", names);
    }

    [Fact]
    public async Task Enum_options_follow_an_options_level_string_enum_converter()
    {
        await using var app = await StartAsync(services => services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase))));
        using var client = app.GetTestClient();

        var status = await FieldAsync(client, "status");
        var values = status.GetProperty("options").GetProperty("inline").EnumerateArray()
            .Select(o => o.GetProperty("value").GetString())
            .ToList();

        // The options-level converter makes the endpoint bind camelCase strings — numeric values would be
        // rejected by a string-enum binder... and these exact strings are what the serializer emits.
        Assert.Contains("pending", values);
        Assert.Contains("shipped", values);
        Assert.DoesNotContain("0", values);
    }

    [Fact]
    public async Task MinLength_is_emitted_from_string_length_and_min_length_annotations()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var note = await FieldAsync(client, "orderNote");
        Assert.Equal(2, note.GetProperty("minLength").GetInt32());
        Assert.Equal(50, note.GetProperty("maxLength").GetInt32());

        var code = await FieldAsync(client, "code");
        Assert.Equal(3, code.GetProperty("minLength").GetInt32());
    }

    [Fact]
    public async Task Localized_prompts_follow_each_requests_culture()
    {
        await using var app = await StartAsync(localize: true);
        using var client = app.GetTestClient();

        // The first request primes the schema cache; a frozen cache would leak French to everyone after.
        using var french = new HttpRequestMessage(HttpMethod.Get, "/orders/7");
        french.Headers.TryAddWithoutValidation("Accept-Language", "fr");
        var frenchStatus = FieldOf(JsonDocument.Parse(await (await client.SendAsync(french)).Content.ReadAsStringAsync()), "status");
        Assert.Equal("Statut", frenchStatus.GetProperty("prompt").GetString());

        using var english = new HttpRequestMessage(HttpMethod.Get, "/orders/7");
        english.Headers.TryAddWithoutValidation("Accept-Language", "en");
        var englishStatus = FieldOf(JsonDocument.Parse(await (await client.SendAsync(english)).Content.ReadAsStringAsync()), "status");
        Assert.Equal("Status", englishStatus.GetProperty("prompt").GetString());
    }

    private static async Task<List<string>> FieldNamesAsync(HttpClient client)
    {
        var document = JsonDocument.Parse(await client.GetStringAsync("/orders/7"));
        return document.RootElement.GetProperty("_templates").GetProperty("default").GetProperty("properties")
            .EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToList();
    }

    private static async Task<JsonElement> FieldAsync(HttpClient client, string name)
        => FieldOf(JsonDocument.Parse(await client.GetStringAsync("/orders/7")), name);

    private static JsonElement FieldOf(JsonDocument document, string name)
        => document.RootElement.GetProperty("_templates").GetProperty("default").GetProperty("properties")
            .EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);

    private static async Task<WebApplication> StartAsync(Action<IServiceCollection>? configureServices = null, bool localize = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new ContractOrderLinks());
        });
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        if (localize)
        {
            app.UseRequestLocalization(new RequestLocalizationOptions()
                .SetDefaultCulture("en")
                .AddSupportedCultures("en", "fr")
                .AddSupportedUICultures("en", "fr"));
        }

        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ContractOrder(id))).WithName("ContractGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/update", (int id) => TypedResults.NoContent()).WithName("ContractUpdate");
        await app.StartAsync();
        return app;
    }

    private sealed record ContractOrder(int Id);

    private enum ContractStatus
    {
        Pending,
        Shipped,
    }

    private sealed class ContractUpdateInput
    {
        [Display(Name = nameof(ContractPrompts.StatusPrompt), ResourceType = typeof(ContractPrompts))]
        public ContractStatus Status { get; init; }

        [StringLength(50, MinimumLength = 2)]
        public string? OrderNote { get; init; }

        [MinLength(3)]
        public string? Code { get; init; }

        [JsonPropertyName("renamed")]
        public string? CustomField { get; init; }
    }

    private sealed class ContractOrderLinks : LinkConfig<ContractOrder>
    {
        public override void Configure(ILinkBuilder<ContractOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("ContractGetOrder", new { id = order.Id }));
            builder.Affordance("update", order => LinkTarget.Route("ContractUpdate", new { id = order.Id }))
                .Accepts<ContractUpdateInput>();
        }
    }
}
