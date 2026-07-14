using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.Mcp.Tests;

public class CairnMcpSchemaTests
{
    [Fact]
    public async Task Input_property_types_map_to_json_schema_types()
    {
        var schema = await RetagSchemaAsync();
        var properties = schema.GetProperty("properties");

        Assert.Equal("integer", properties.GetProperty("state").GetProperty("type").GetString());   // enum, numeric wire form
        Assert.Contains(properties.GetProperty("state").GetProperty("enum").EnumerateArray(), e => e.GetInt64() == 1);
        Assert.Equal("integer", properties.GetProperty("maybeState").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("rush").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("weight").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("price").GetProperty("type").GetString());
        Assert.Equal("date-time", properties.GetProperty("when").GetProperty("format").GetString());
        Assert.Equal("date-time", properties.GetProperty("stamp").GetProperty("format").GetString());
        Assert.Equal("date", properties.GetProperty("day").GetProperty("format").GetString());
        Assert.Equal("time", properties.GetProperty("at").GetProperty("format").GetString());
        Assert.Equal("uuid", properties.GetProperty("token").GetProperty("format").GetString());
        Assert.Equal("uri", properties.GetProperty("site").GetProperty("format").GetString());

        // Collections and nested objects are accepted as-is: the endpoint's binder stays authoritative.
        Assert.False(properties.GetProperty("tags").TryGetProperty("type", out _));
    }

    [Fact]
    public async Task Validation_annotations_become_schema_constraints()
    {
        var schema = await RetagSchemaAsync();
        var properties = schema.GetProperty("properties");

        Assert.Equal(1, properties.GetProperty("rating").GetProperty("minimum").GetDouble());
        Assert.Equal(5, properties.GetProperty("rating").GetProperty("maximum").GetDouble());
        Assert.Equal("^[a-z]+$", properties.GetProperty("code").GetProperty("pattern").GetString());
        Assert.Equal(10, properties.GetProperty("code").GetProperty("maxLength").GetInt32());
        Assert.Equal(2, properties.GetProperty("code").GetProperty("minLength").GetInt32());
        Assert.Equal(4, properties.GetProperty("shortNote").GetProperty("maxLength").GetInt32());

        // A parameterless [MaxLength] (provider-defined maximum) and a non-numeric [Range] add no constraints.
        Assert.False(properties.GetProperty("unbounded").TryGetProperty("maxLength", out _));
        Assert.False(properties.GetProperty("flagged").TryGetProperty("minimum", out _));

        // [MinLength] works standalone; a zero minimum (explicit, or StringLength's default) adds nothing.
        Assert.Equal(2, properties.GetProperty("pair").GetProperty("minLength").GetInt32());
        Assert.False(properties.GetProperty("loose").TryGetProperty("minLength", out _));
        Assert.False(properties.GetProperty("capped").TryGetProperty("minLength", out _));
        Assert.Equal(8, properties.GetProperty("capped").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public async Task Injected_hypermedia_contract_properties_never_become_schema_fields()
    {
        // widget_clone accepts Widget, whose serializer contract carries Cairn's injected _links/_actions
        // properties — synthetic members with no PropertyInfo behind them, which the schema must skip.
        await using var app = await McpTestApp.StartAsync(
            options => options.AddResource<Widget>("widget", (_, _) => new ValueTask<Widget?>(new Widget(1))),
            configureCairn: options => options.AddLinks(new WidgetLinks()));
        await using var client = await McpTestApp.ConnectAsync(app);

        var clone = (await client.ListToolsAsync()).Single(t => t.Name == "widget_clone");

        var properties = clone.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("id", out _));
        Assert.False(properties.TryGetProperty("_links", out _));
        Assert.False(properties.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task An_enum_converter_that_writes_neither_string_nor_number_falls_back_to_numeric_values()
    {
        var schema = await RetagSchemaAsync();
        var odd = schema.GetProperty("properties").GetProperty("odd");

        Assert.Equal("integer", odd.GetProperty("type").GetString());
        Assert.Equal([0L, 1L], odd.GetProperty("enum").EnumerateArray().Select(e => e.GetInt64()).ToArray());
    }

    [Fact]
    public async Task Required_members_and_non_nullable_references_are_required()
    {
        var schema = await RetagSchemaAsync();
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("must", required);       // the C# `required` modifier
        Assert.Contains("definite", required);   // a non-nullable reference under NRT
        Assert.DoesNotContain("code", required); // nullable stays optional
        Assert.DoesNotContain("rating", required);
    }

    [Fact]
    public async Task Descriptions_and_wire_names_follow_the_hosts_serializer_contract()
    {
        var schema = await RetagSchemaAsync();
        var properties = schema.GetProperty("properties");

        Assert.Equal("internal note", properties.GetProperty("note").GetProperty("description").GetString());
        Assert.Equal("Friendly label", properties.GetProperty("friendly").GetProperty("description").GetString());
        Assert.True(properties.TryGetProperty("custom_name", out _));   // [JsonPropertyName] wins
        Assert.False(properties.TryGetProperty("renamed", out _));
    }

    [Fact]
    public async Task String_serialized_enums_list_their_wire_strings()
    {
        var schema = await RetagSchemaAsync(builder =>
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter())));

        var state = schema.GetProperty("properties").GetProperty("state");
        Assert.Equal("string", state.GetProperty("type").GetString());
        Assert.Contains(state.GetProperty("enum").EnumerateArray(), e => e.GetString() == "Active");
    }

    private static async Task<JsonElement> RetagSchemaAsync(Action<WebApplicationBuilder>? configureBuilder = null)
    {
        await using var app = await McpTestApp.StartAsync(
            options => options.AddResource<Widget>("widget", (_, _) => new ValueTask<Widget?>(new Widget(1))),
            configureBuilder,
            options => options.AddLinks(new WidgetLinks()));
        await using var client = await McpTestApp.ConnectAsync(app);

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "widget_retag");
        using var document = JsonDocument.Parse(tool.JsonSchema.GetRawText());
        return document.RootElement.Clone();
    }
}

public enum WidgetState
{
    Draft = 0,
    Active = 1,
}

public sealed record Widget(int Id);

public sealed class RetagRequest
{
    public WidgetState State { get; init; }

    public WidgetState? MaybeState { get; init; }

    public bool Rush { get; init; }

    public double Weight { get; init; }

    public decimal Price { get; init; }

    public DateTime When { get; init; }

    public DateTimeOffset Stamp { get; init; }

    public DateOnly Day { get; init; }

    public TimeOnly At { get; init; }

    public Guid Token { get; init; }

    public Uri? Site { get; init; }

    public List<string>? Tags { get; init; }

    [Range(1, 5)]
    public int Rating { get; init; }

    [RegularExpression("^[a-z]+$")]
    [StringLength(10, MinimumLength = 2)]
    public string? Code { get; init; }

    [MaxLength(4)]
    public string? ShortNote { get; init; }

    [Description("internal note")]
    public string? Note { get; init; }

    public required string Must { get; init; }

    public string Definite { get; init; } = "";

    [MaxLength]
    public string? Unbounded { get; init; }

    [Display]
    public string? Bare { get; init; }

    [Range(typeof(bool), "false", "true")]
    public bool Flagged { get; init; }

    public string this[int index] => string.Empty;   // indexers never become schema fields

    [Display(Name = "Friendly label")]
    public string? Friendly { get; init; }

    [JsonPropertyName("custom_name")]
    public string? Renamed { get; init; }

    [MinLength(2)]
    public List<int>? Pair { get; init; }

    [MinLength(0)]
    public string? Loose { get; init; }

    [StringLength(8)]
    public string? Capped { get; init; }

    public OddEnum Odd { get; init; }

    public string? Sink { set { _ = value; } }   // set-only: skipped by the reflection fallback
}

/// <summary>An enum whose converter emits neither a string nor a number, forcing the numeric fallback.</summary>
[JsonConverter(typeof(OddEnumConverter))]
public enum OddEnum
{
    No = 0,
    Yes = 1,
}

public sealed class OddEnumConverter : JsonConverter<OddEnum>
{
    public override OddEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetBoolean() ? OddEnum.Yes : OddEnum.No;

    public override void Write(Utf8JsonWriter writer, OddEnum value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value == OddEnum.Yes);
}

public sealed class WidgetLinks : LinkConfig<Widget>
{
    public override void Configure(ILinkBuilder<Widget> builder)
    {
        builder.Self(w => LinkTarget.Uri($"/widgets/{w.Id}"));
        builder.Affordance("retag", w => LinkTarget.Uri($"/widgets/{w.Id}/retag"))
            .Put()
            .Accepts<RetagRequest>();
        builder.Affordance("clone", w => LinkTarget.Uri($"/widgets/{w.Id}/clone"))
            .Post()
            .Accepts<Widget>();   // an input type that itself carries hypermedia (it has a LinkConfig)
    }
}
