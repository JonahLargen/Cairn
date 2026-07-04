using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Derives HAL-FORMS template properties from an input type's data annotations. Field names come from the
/// serializer contract that will bind the submitted payload — <c>[JsonPropertyName]</c> and the host's
/// <c>PropertyNamingPolicy</c> (camelCase, snake_case, ...) — so a generic client builds payloads whose
/// fields the endpoint actually reads. Results are cached per serializer contract and input type, and per UI
/// culture when any prompt is localizable (<c>[Display(ResourceType = ...)]</c>).
/// </summary>
internal static class HalFormsSchema
{
    // Localizable types key their cache entries by CultureInfo.CurrentUICulture.Name, which under
    // RequestLocalization's AcceptLanguageHeaderRequestCultureProvider is derived from a client-controlled
    // header — an attacker (or just a diverse client population) can mint unbounded distinct culture names.
    // Once the cache is full, schemas for unseen (type, culture) pairs are rebuilt per request instead of
    // being cached; correctness is unaffected.
    private const int MaxCacheEntries = 1024;

    private static readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<(Type Input, string Culture), IReadOnlyList<HalFormsProperty>>> Caches = new();

    // Whether a type's prompts resolve through localized resources; only those types cache per culture.
    private static readonly ConcurrentDictionary<Type, bool> Localizable = new();

    private static readonly IReadOnlyList<HalFormsProperty> Empty = [];

    public static IReadOnlyList<HalFormsProperty> For([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type? input, JsonSerializerOptions serializer)
    {
        if (input is null)
        {
            return Empty;
        }

        var cache = Caches.GetOrCreateValue(serializer);
        if (Localizable.TryGetValue(input, out var localized)
            && cache.TryGetValue((input, localized ? CultureInfo.CurrentUICulture.Name : string.Empty), out var cached))
        {
            return cached;
        }

        var properties = Build(input, serializer, out var localizable);
        Localizable[input] = localizable;

        // The count/add race can overshoot the cap by a few concurrent entries; that slack is fine — the
        // point is that the cache cannot grow linearly with distinct request cultures.
        if (cache.Count >= MaxCacheEntries)
        {
            return properties;
        }

        return cache.GetOrAdd((input, localizable ? CultureInfo.CurrentUICulture.Name : string.Empty), properties);
    }

    private static IReadOnlyList<HalFormsProperty> Build([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type input, JsonSerializerOptions serializer, out bool localizable)
    {
        var properties = new List<HalFormsProperty>();

        // Not thread-safe; one per build (the result is cached anyway).
        var nullability = new NullabilityInfoContext();
        localizable = false;

        foreach (var (property, name) in ContractProperties(input, serializer))
        {
            localizable |= property.GetCustomAttribute<DisplayAttribute>()?.ResourceType is not null;
            properties.Add(BuildProperty(property, name, nullability, serializer));
        }

        return properties;
    }

    // The members the endpoint will bind are the serializer contract's properties, under the contract's wire
    // names. Cairn's own injected contract properties have no member behind them and are skipped; fields stay
    // out, as before. Falls back to reflection plus the options' naming policy when the resolver cannot
    // produce an object contract for the type (e.g. a source-gen-only resolver that doesn't know it).
    private static IEnumerable<(PropertyInfo Property, string Name)> ContractProperties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type input, JsonSerializerOptions serializer)
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

    private static HalFormsProperty BuildProperty(PropertyInfo property, string name, NullabilityInfoContext nullability, JsonSerializerOptions serializer)
    {
        var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var range = property.GetCustomAttribute<RangeAttribute>();
        var display = property.GetCustomAttribute<DisplayAttribute>();

        return new HalFormsProperty(name)
        {
            Prompt = display?.GetName(),
            Required = IsRequired(property, nullability) ? true : null,
            ReadOnly = IsReadOnly(property) ? true : null,
            Type = property.GetCustomAttribute<EmailAddressAttribute>() is not null ? "email" : MapType(underlying),
            Placeholder = display?.GetPrompt(),

            // The .NET pattern is carried verbatim — no translation. HAL-FORMS clients validate regex with
            // their own engine (typically ECMAScript), so .NET-only constructs ((?i) inline options,
            // balancing groups, (?'name'...), ...) won't work for non-Cairn clients; see
            // docs/articles/affordances-and-forms.md.
            Regex = property.GetCustomAttribute<RegularExpressionAttribute>()?.Pattern,
            MinLength = MinLengthOf(property),
            MaxLength = property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength ?? MaxLengthOf(property),
            Min = ToDouble(range?.Minimum),
            Max = ToDouble(range?.Maximum),
            Value = DefaultValueOf(property, underlying, serializer),
            Options = BuildOptions(underlying, serializer),
        };
    }

    // Required when annotated ([Required]), declared with the C# `required` modifier, or a non-nullable
    // reference type under nullable reference types.
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

    private static bool IsReadOnly(PropertyInfo property)
        => property.GetCustomAttribute<EditableAttribute>() is { AllowEdit: false }
            || property.GetCustomAttribute<ReadOnlyAttribute>() is { IsReadOnly: true };

    private static string? DefaultValueOf(PropertyInfo property, Type underlying, JsonSerializerOptions serializer)
    {
        var value = property.GetCustomAttribute<DefaultValueAttribute>()?.Value;
        if (value is null)
        {
            return null;
        }

        // An enum default given as the enum member ([DefaultValue(Status.Shipped)]) must be emitted in the
        // same wire form as the options list below (numeric, or the converter's string) — the member name
        // Convert.ToString would produce ("Shipped") matches no option, so it preselects nothing. A default
        // supplied as a raw string or number is passed through the formatting below unchanged.
        if (underlying.IsEnum && value.GetType() == underlying)
        {
            return WireValueOf(value, underlying, serializer);
        }

        return value switch
        {
            bool flag => flag ? "true" : "false",   // match the bool options values below
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    // An enum-typed (or bool) property becomes a fixed list of selectable values. Option values must
    // round-trip through the host's binder, so each member is serialized through the host's options: numeric
    // under the default converter, or the exact wire string when the enum serializes as strings — whether the
    // converter is declared on the enum type or added to the serializer options, naming policy and all.
    private static HalFormsOptions? BuildOptions(Type underlying, JsonSerializerOptions serializer)
    {
        if (underlying == typeof(bool))
        {
            return new HalFormsOptions([new HalFormsOption("True", "true"), new HalFormsOption("False", "false")]);
        }

        if (!underlying.IsEnum)
        {
            return null;
        }

        // GetValuesAsUnderlyingType + ToObject instead of GetValues: the latter needs runtime code to
        // create the enum array under Native AOT. Both return values in the same (unsigned magnitude) order
        // as GetNames.
        var names = Enum.GetNames(underlying);
        var values = Enum.GetValuesAsUnderlyingType(underlying);
        var options = new List<HalFormsOption>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            options.Add(new HalFormsOption(names[i], WireValueOf(Enum.ToObject(underlying, values.GetValue(i)!), underlying, serializer)));
        }

        return options.Count > 0 ? new HalFormsOptions(options) : null;
    }

    private static string WireValueOf(object value, Type enumType, JsonSerializerOptions serializer)
    {
        try
        {
            // Serializing through the resolved contract honors the host's enum converters without the
            // reflection-based Serialize(object, Type, options) overload; a missing contract (source-gen-only
            // host without this enum) throws and falls back to the declared name below.
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, serializer.GetTypeInfo(enumType)));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { } text)
            {
                return text;
            }

            if (root.ValueKind == JsonValueKind.Number)
            {
                return root.GetRawText();
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
        }

        // A converter that emits something other than a string or number (or none at all): fall back to the
        // default binder's numeric form.
        return Enum.Format(enumType, value, "D");
    }

    private static string? MapType(Type type)
    {
        // bool has no HAL-FORMS input type of its own; it is described via a two-value options list instead
        // ("checkbox" is not a valid HAL-FORMS type).
        if (type == typeof(bool))
        {
            return null;
        }

        if (type == typeof(DateOnly))
        {
            return "date";
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "datetime-local";
        }

        if (type == typeof(TimeOnly))
        {
            return "time";
        }

        if (IsNumeric(type))
        {
            return "number";
        }

        return type == typeof(string) ? "text" : null;
    }

    private static bool IsNumeric(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte)
            || type == typeof(decimal) || type == typeof(double) || type == typeof(float);

    private static int? MaxLengthOf(PropertyInfo property)
    {
        var length = property.GetCustomAttribute<MaxLengthAttribute>()?.Length;
        return length is int value and >= 0 ? value : null;
    }

    // [StringLength(MinimumLength = ...)] or [MinLength], mirroring how maxLength is derived.
    private static int? MinLengthOf(PropertyInfo property)
    {
        var length = property.GetCustomAttribute<StringLengthAttribute>()?.MinimumLength;
        if (length is > 0)
        {
            return length;
        }

        length = property.GetCustomAttribute<MinLengthAttribute>()?.Length;
        return length is > 0 ? length : null;
    }

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
