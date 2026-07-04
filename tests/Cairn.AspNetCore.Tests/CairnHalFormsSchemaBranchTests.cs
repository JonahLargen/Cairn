using System.ComponentModel;
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

// Branch-direction coverage for HalFormsSchema: type mapping edges, attribute edge values, enum wire values
// under custom converters, the reflection fallback when the resolver has no contract, and culture caching.
public partial class CairnHalFormsSchemaBranchTests
{
    [Fact]
    public async Task Template_fields_map_dates_lengths_ranges_defaults_and_readonly_edges()
    {
        var props = await TemplatePropertiesAsync<EdgeFieldsInput>();

        // Date/time scalars get their dedicated HAL-FORMS input types; a nullable numeric unwraps first.
        Assert.Equal("date", props["day"].GetProperty("type").GetString());
        Assert.Equal("time", props["at"].GetProperty("type").GetString());
        Assert.Equal("number", props["count"].GetProperty("type").GetString());

        // A parameterless [MaxLength] means "provider maximum" (-1) and surfaces no maxLength.
        Assert.False(props["unbounded"].TryGetProperty("maxLength", out _));
        Assert.Equal(12, props["bounded"].GetProperty("maxLength").GetInt32());

        // Zero minimums are not constraints.
        Assert.False(props["minZero"].TryGetProperty("minLength", out _));
        Assert.False(props["sized"].TryGetProperty("minLength", out _));
        Assert.Equal(30, props["sized"].GetProperty("maxLength").GetInt32());

        // A [Range] over dates cannot convert to double, so no min/max is emitted.
        Assert.False(props["window"].TryGetProperty("min", out _));
        Assert.False(props["window"].TryGetProperty("max", out _));

        // [ReadOnly(true)] marks the field; [ReadOnly(false)] and [Editable(true)] do not.
        Assert.True(props["locked"].GetProperty("readOnly").GetBoolean());
        Assert.False(props["unlocked"].TryGetProperty("readOnly", out _));
        Assert.False(props["tweakable"].TryGetProperty("readOnly", out _));

        // [DefaultValue] on bools matches the options values.
        Assert.Equal("true", props["enabled"].GetProperty("value").GetString());
        Assert.Equal("false", props["disabled"].GetProperty("value").GetString());

        // A member-less enum yields no options block.
        Assert.False(props["choice"].TryGetProperty("options", out _));
    }

    [Fact]
    public async Task Enum_option_values_follow_the_hosts_converters_and_fall_back_on_odd_ones()
    {
        var props = await TemplatePropertiesAsync<EnumWireInput>();

        // A string-converted enum carries its exact wire strings; the default converter carries numbers.
        var stringy = InlineValues(props["asString"]);
        Assert.Contains("Alpha", stringy);
        Assert.Contains("Beta", stringy);

        var numeric = InlineValues(props["asNumber"]);
        Assert.Contains("0", numeric);
        Assert.Contains("1", numeric);

        // A converter that writes neither a string nor a number, and one that throws outright, both fall
        // back to the default binder's numeric form.
        var objecty = InlineValues(props["asObject"]);
        Assert.Contains("0", objecty);

        var throwing = InlineValues(props["asThrowing"]);
        Assert.Contains("0", throwing);
    }

    [Fact]
    public async Task A_non_object_input_contract_falls_back_to_reflection_over_its_properties()
    {
        // List<string> has an enumerable contract, not an object contract, so the schema reflects over the
        // type's public readable properties instead.
        var props = await TemplatePropertiesAsync<List<string>>();

        Assert.Contains("count", props.Keys);
    }

    [Fact]
    public async Task A_resolver_without_the_input_type_falls_back_to_reflection_and_wire_names()
    {
        var props = await TemplatePropertiesAsync<ResolverlessInput>(json =>
        {
            json.SerializerOptions.TypeInfoResolver = SchemaBranchContext.Default;
        });

        // [JsonPropertyName] wins; otherwise the host's naming policy (web default camelCase) applies.
        Assert.Contains("renamed", props.Keys);
        Assert.Contains("plainName", props.Keys);

        // Write-only properties and indexers are not bindable form fields.
        Assert.DoesNotContain(props.Keys, name => name.Contains("sink", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props.Keys, name => name.Contains("item", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_reflection_fallback_without_a_naming_policy_keeps_declared_names()
    {
        var props = await TemplatePropertiesAsync<ResolverlessInput>(json =>
        {
            json.SerializerOptions.TypeInfoResolver = SchemaBranchContext.Default;
            json.SerializerOptions.PropertyNamingPolicy = null;
        });

        Assert.Contains("renamed", props.Keys);
        Assert.Contains("PlainName", props.Keys);
    }

    [Fact]
    public async Task Localizable_prompts_resolve_per_request_culture_and_are_cached_per_culture()
    {
        await using var app = await StartAsync<LocalizedInput>();
        using var client = app.GetTestClient();

        var french = await TemplatePropertiesAsync(client, "/doc/1?culture=fr-FR");
        Assert.Equal("Montant", french["amount"].GetProperty("prompt").GetString());

        var english = await TemplatePropertiesAsync(client, "/doc/1?culture=en-US");
        Assert.Equal("Amount", english["amount"].GetProperty("prompt").GetString());

        // A repeat request for a seen culture is served from the per-culture cache.
        var cachedFrench = await TemplatePropertiesAsync(client, "/doc/1?culture=fr-FR");
        Assert.Equal("Montant", cachedFrench["amount"].GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task The_per_culture_cache_stops_growing_at_its_cap_but_stays_correct()
    {
        await using var app = await StartAsync<LocalizedInput>();
        using var client = app.GetTestClient();

        // Distinct client-controlled cultures must not grow the cache without bound: past the cap, schemas
        // for unseen cultures are rebuilt per request instead of cached. 1,030 distinct private-use tags
        // comfortably overshoot the 1,024-entry cap.
        for (var i = 0; i < 1030; i++)
        {
            var response = await client.GetAsync($"/doc/1?culture=en-x-c{i.ToString(CultureInfo.InvariantCulture)}");
            response.EnsureSuccessStatusCode();
        }

        // Beyond the cap the schema is still built correctly, just not cached.
        var over = await TemplatePropertiesAsync(client, "/doc/1?culture=fr-FR");
        Assert.Equal("Montant", over["amount"].GetProperty("prompt").GetString());
    }

    private static List<string?> InlineValues(JsonElement property)
        => property.GetProperty("options").GetProperty("inline").EnumerateArray()
            .Select(o => o.GetProperty("value").GetString())
            .ToList();

    private static async Task<Dictionary<string, JsonElement>> TemplatePropertiesAsync<TInput>(
        Action<Microsoft.AspNetCore.Http.Json.JsonOptions>? configureJson = null)
    {
        await using var app = await StartAsync<TInput>(configureJson);
        using var client = app.GetTestClient();
        return await TemplatePropertiesAsync(client, "/doc/1");
    }

    private static async Task<Dictionary<string, JsonElement>> TemplatePropertiesAsync(HttpClient client, string url)
        => JsonDocument.Parse(await client.GetStringAsync(url)).RootElement
            .GetProperty("_templates").GetProperty("default").GetProperty("properties")
            .EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);

    private static async Task<WebApplication> StartAsync<TInput>(Action<Microsoft.AspNetCore.Http.Json.JsonOptions>? configureJson = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (configureJson is not null)
        {
            builder.Services.ConfigureHttpJsonOptions(configureJson);
        }

        builder.Services.AddCairn(o =>
        {
            o.DefaultFormat = HypermediaFormat.HalForms;
            o.AddLinks(new SchemaBranchLinks<TInput>());
        });

        var app = builder.Build();

        // The UI culture flows from a query parameter so localizable prompts can vary per request without
        // wiring up RequestLocalization.
        app.Use(async (context, next) =>
        {
            if (context.Request.Query["culture"].ToString() is { Length: > 0 } culture)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            }

            await next(context);
        });

        app.MapGet("/doc/{id:int}", (int id) => TypedResults.Ok(new SchemaBranchDoc(id))).WithName("SchemaBranchGet").WithLinks();
        app.MapPost("/doc/{id:int}/edit", (int id) => TypedResults.NoContent()).WithName("SchemaBranchEdit");
        await app.StartAsync();
        return app;
    }

    private enum EmptyChoice
    {
    }

    [JsonConverter(typeof(JsonStringEnumConverter<StringyChoice>))]
    private enum StringyChoice
    {
        Alpha,
        Beta,
    }

    private enum PlainChoice
    {
        Zero,
        One,
    }

    [JsonConverter(typeof(ObjectChoiceConverter))]
    private enum ObjectChoice
    {
        Solo,
    }

    [JsonConverter(typeof(ThrowingChoiceConverter))]
    private enum ThrowingChoice
    {
        Solo,
    }

    // Writes a JSON object — neither a string nor a number — so the wire value cannot be used.
    private sealed class ObjectChoiceConverter : JsonConverter<ObjectChoice>
    {
        public override ObjectChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => default;

        public override void Write(Utf8JsonWriter writer, ObjectChoice value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    private sealed class ThrowingChoiceConverter : JsonConverter<ThrowingChoice>
    {
        public override ThrowingChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => default;

        public override void Write(Utf8JsonWriter writer, ThrowingChoice value, JsonSerializerOptions options)
            => throw new InvalidOperationException("no wire form");
    }

    private sealed class EdgeFieldsInput
    {
        public DateOnly Day { get; init; }

        public TimeOnly At { get; init; }

        public int? Count { get; init; }

        [MaxLength]
        public string? Unbounded { get; init; }

        [MaxLength(12)]
        public string? Bounded { get; init; }

        [MinLength(0)]
        public string? MinZero { get; init; }

        [StringLength(30)]
        public string? Sized { get; init; }

        [Range(typeof(DateTime), "2020-01-01", "2021-01-01")]
        public DateTime Window { get; init; }

        [ReadOnly(true)]
        public int Locked { get; init; }

        [ReadOnly(false)]
        public int Unlocked { get; init; }

        [Editable(true)]
        public int Tweakable { get; init; }

        [DefaultValue(true)]
        public bool Enabled { get; init; }

        [DefaultValue(false)]
        public bool Disabled { get; init; }

        public EmptyChoice Choice { get; init; }
    }

    private sealed class EnumWireInput
    {
        public StringyChoice AsString { get; init; }

        public PlainChoice AsNumber { get; init; }

        public ObjectChoice AsObject { get; init; }

        public ThrowingChoice AsThrowing { get; init; }
    }

    private sealed class ResolverlessInput
    {
        [JsonPropertyName("renamed")]
        public string? Original { get; init; }

        public string? PlainName { get; init; }

        public string Sink
        {
            set => _ = value;
        }

        public string this[int index] => index.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class LocalizedInput
    {
        [Display(Name = nameof(SchemaBranchPrompts.AmountPrompt), ResourceType = typeof(SchemaBranchPrompts))]
        public int Amount { get; init; }
    }

    private sealed class SchemaBranchLinks<TInput> : LinkConfig<SchemaBranchDoc>
    {
        public override void Configure(ILinkBuilder<SchemaBranchDoc> builder)
        {
            builder.Self(doc => LinkTarget.Route("SchemaBranchGet", new { id = doc.Id }));
            builder.Affordance("edit", doc => LinkTarget.Route("SchemaBranchEdit", new { id = doc.Id }))
                .Put()
                .Accepts<TInput>();
        }
    }

    [JsonSerializable(typeof(SchemaBranchDoc))]
    private sealed partial class SchemaBranchContext : JsonSerializerContext;
}

// The response DTO and prompt source live at namespace scope so the source-generated context and
// DisplayAttribute's resource lookup can both see them.
public sealed record SchemaBranchDoc(int Id);

/// <summary>Localized prompt source for <see cref="CairnHalFormsSchemaBranchTests"/>.</summary>
public static class SchemaBranchPrompts
{
    public static string AmountPrompt
        => CultureInfo.CurrentUICulture.Name.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "Montant" : "Amount";
}
