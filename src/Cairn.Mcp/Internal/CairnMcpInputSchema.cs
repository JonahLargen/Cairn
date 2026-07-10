using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cairn.Mcp.Internal;

/// <summary>
/// Derives a tool's JSON Schema from an affordance's declared input type. Field names come from the serializer
/// contract that will bind the submitted payload — <c>[JsonPropertyName]</c> and the host's
/// <c>PropertyNamingPolicy</c> — so the agent builds payloads whose fields the endpoint actually reads
/// (mirroring how HAL-FORMS templates are derived).
/// </summary>
internal static class CairnMcpInputSchema
{
    /// <summary>Builds the <c>inputSchema</c> for a tool taking an optional <c>id</c> plus the input type's fields.</summary>
    public static JsonElement Build(
        string? idDescription,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type? input,
        JsonSerializerOptions serializer)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        if (idDescription is not null)
        {
            properties["id"] = new JsonObject { ["type"] = "string", ["description"] = idDescription };
            required.Add((JsonNode)"id");
        }

        if (input is not null)
        {
            var nullability = new NullabilityInfoContext();
            foreach (var (property, name) in ContractProperties(input, serializer))
            {
                properties[name] = PropertySchema(property, serializer);
                if (IsRequired(property, nullability))
                {
                    required.Add((JsonNode)name);
                }
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    // The members the endpoint will bind are the serializer contract's properties, under the contract's wire
    // names. Falls back to reflection plus the options' naming policy when the resolver cannot produce an
    // object contract for the type (e.g. a source-gen-only resolver that doesn't know it).
    private static IEnumerable<(PropertyInfo Property, string Name)> ContractProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type input,
        JsonSerializerOptions serializer)
    {
        JsonTypeInfo? contract = null;
        try
        {
            contract = serializer.GetTypeInfo(input);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
        }

        if (contract is { Kind: JsonTypeInfoKind.Object })
        {
            foreach (var member in contract.Properties)
            {
                if (member.AttributeProvider is PropertyInfo property && property.GetIndexParameters().Length == 0)
                {
                    yield return (property, member.Name);
                }
            }

            yield break;
        }

        foreach (var property in input.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? serializer.PropertyNamingPolicy?.ConvertName(property.Name)
                    ?? property.Name;
                yield return (property, name);
            }
        }
    }

    private static JsonObject PropertySchema(PropertyInfo property, JsonSerializerOptions serializer)
    {
        var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var schema = TypeSchema(underlying, serializer);

        var description = property.GetCustomAttribute<DisplayAttribute>()?.GetName()
            ?? property.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (description is not null)
        {
            schema["description"] = description;
        }

        if (property.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            if (ToDouble(range.Minimum) is { } minimum)
            {
                schema["minimum"] = minimum;
            }

            if (ToDouble(range.Maximum) is { } maximum)
            {
                schema["maximum"] = maximum;
            }
        }

        if (property.GetCustomAttribute<RegularExpressionAttribute>()?.Pattern is { } pattern)
        {
            schema["pattern"] = pattern;
        }

        var stringLength = property.GetCustomAttribute<StringLengthAttribute>();
        if ((stringLength?.MaximumLength ?? MaxLengthOf(property)) is { } maxLength)
        {
            schema["maxLength"] = maxLength;
        }

        if ((MinLengthOf(stringLength) ?? property.GetCustomAttribute<MinLengthAttribute>()?.Length) is { } minLength and > 0)
        {
            schema["minLength"] = minLength;
        }

        return schema;
    }

    private static JsonObject TypeSchema(Type type, JsonSerializerOptions serializer)
    {
        if (type.IsEnum)
        {
            return EnumSchema(type, serializer);
        }

        if (type == typeof(string))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (IsInteger(type))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
        {
            return new JsonObject { ["type"] = "number" };
        }

        if (FormatOf(type) is { } format)
        {
            return new JsonObject { ["type"] = "string", ["format"] = format };
        }

        // Anything else (nested objects, collections, ...) is accepted as-is; the endpoint's binder and
        // validation remain the authority on its shape.
        return [];
    }

    // An enum's selectable values must round-trip through the host's binder, so each member is serialized
    // through the host's options: numeric under the default converter, or the exact wire string when the enum
    // serializes as strings — whether the converter is declared on the enum type or added to the options.
    private static JsonObject EnumSchema(Type type, JsonSerializerOptions serializer)
    {
        // GetValuesAsUnderlyingType + ToObject instead of GetValues: the latter needs runtime code to create
        // the enum array under Native AOT.
        var values = Enum.GetValuesAsUnderlyingType(type);
        var wire = new JsonArray();
        var numeric = false;
        for (var i = 0; i < values.Length; i++)
        {
            var node = WireValueOf(Enum.ToObject(type, values.GetValue(i)!), type, serializer);
            numeric |= node is not JsonValue value || value.GetValueKind() != JsonValueKind.String;
            wire.Add(node);
        }

        return new JsonObject { ["type"] = numeric ? "integer" : "string", ["enum"] = wire };
    }

    private static JsonNode WireValueOf(object value, Type enumType, JsonSerializerOptions serializer)
    {
        try
        {
            // Serializing through the resolved contract honors the host's enum converters without the
            // reflection-based Serialize(object, Type, options) overload; a missing contract (source-gen-only
            // host without this enum) throws and falls back to the numeric form below.
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, serializer.GetTypeInfo(enumType)));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { } text)
            {
                return JsonValue.Create(text);
            }

            if (root.ValueKind == JsonValueKind.Number)
            {
                return JsonValue.Create(root.GetInt64());
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException or FormatException)
        {
        }

        // A converter that emits something other than a string or number (or none at all): fall back to the
        // default binder's numeric form.
        return JsonValue.Create(long.Parse(Enum.Format(enumType, value, "D"), CultureInfo.InvariantCulture));
    }

    // Required when annotated ([Required]), declared with the C# `required` modifier, or a non-nullable
    // reference type under nullable reference types (mirroring the HAL-FORMS derivation).
    private static bool IsRequired(PropertyInfo property, NullabilityInfoContext nullability)
    {
        if (property.GetCustomAttribute<RequiredAttribute>() is not null
            || property.GetCustomAttribute<RequiredMemberAttribute>() is not null)
        {
            return true;
        }

        if (property.PropertyType.IsValueType)
        {
            return false;
        }

        return nullability.Create(property).WriteState == NullabilityState.NotNull;
    }

    private static bool IsInteger(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

    private static string? FormatOf(Type type)
    {
        if (type == typeof(DateOnly))
        {
            return "date";
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "date-time";
        }

        if (type == typeof(TimeOnly))
        {
            return "time";
        }

        if (type == typeof(Guid))
        {
            return "uuid";
        }

        return type == typeof(Uri) ? "uri" : null;
    }

    private static int? MaxLengthOf(PropertyInfo property)
    {
        var length = property.GetCustomAttribute<MaxLengthAttribute>()?.Length;
        return length is int value and >= 0 ? value : null;
    }

    private static int? MinLengthOf(StringLengthAttribute? stringLength)
        => stringLength?.MinimumLength is int value and > 0 ? value : null;

    private static double? ToDouble(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
