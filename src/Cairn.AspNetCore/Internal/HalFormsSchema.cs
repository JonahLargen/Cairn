using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.AspNetCore.Internal;

/// <summary>Derives HAL-FORMS template properties from an input type's data annotations (cached per type).</summary>
internal static class HalFormsSchema
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<HalFormsProperty>> Cache = new();
    private static readonly IReadOnlyList<HalFormsProperty> Empty = [];

    public static IReadOnlyList<HalFormsProperty> For(Type? input)
        => input is null ? Empty : Cache.GetOrAdd(input, Build);

    private static IReadOnlyList<HalFormsProperty> Build(Type input)
    {
        var properties = new List<HalFormsProperty>();

        // Not thread-safe; one per build (the result is cached anyway).
        var nullability = new NullabilityInfoContext();

        foreach (var property in input.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                properties.Add(BuildProperty(property, nullability));
            }
        }

        return properties;
    }

    private static HalFormsProperty BuildProperty(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var range = property.GetCustomAttribute<RangeAttribute>();
        var display = property.GetCustomAttribute<DisplayAttribute>();

        return new HalFormsProperty(JsonNamingPolicy.CamelCase.ConvertName(property.Name))
        {
            Prompt = display?.GetName(),
            Required = IsRequired(property, nullability) ? true : null,
            ReadOnly = IsReadOnly(property) ? true : null,
            Type = property.GetCustomAttribute<EmailAddressAttribute>() is not null ? "email" : MapType(underlying),
            Placeholder = display?.GetPrompt(),
            Regex = property.GetCustomAttribute<RegularExpressionAttribute>()?.Pattern,
            MaxLength = property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength ?? MaxLengthOf(property),
            Min = ToDouble(range?.Minimum),
            Max = ToDouble(range?.Maximum),
            Value = DefaultValueOf(property),
            Options = BuildOptions(underlying),
        };
    }

    // Required when annotated ([Required]), declared with the C# `required` modifier, or a non-nullable
    // reference type under nullable reference types.
    private static bool IsRequired(PropertyInfo property, NullabilityInfoContext nullability)
    {
        if (property.GetCustomAttribute<RequiredAttribute>() is not null
            || property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null)
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

    private static string? DefaultValueOf(PropertyInfo property)
        => property.GetCustomAttribute<DefaultValueAttribute>()?.Value switch
        {
            null => null,
            bool flag => flag ? "true" : "false",   // match the bool options values below
            var value => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

    // An enum-typed (or bool) property becomes a fixed list of selectable values. Option values must round-trip
    // through the host's model binder: the default System.Text.Json binder reads enums as numbers, so emit the
    // underlying numeric value (with the name as the prompt) unless the enum opts into string serialization.
    private static HalFormsOptions? BuildOptions(Type underlying)
    {
        if (underlying == typeof(bool))
        {
            return new HalFormsOptions([new HalFormsOption("True", "true"), new HalFormsOption("False", "false")]);
        }

        if (!underlying.IsEnum)
        {
            return null;
        }

        var asStrings = SerializesAsString(underlying);
        var options = new List<HalFormsOption>();
        foreach (var name in Enum.GetNames(underlying))
        {
            var value = asStrings ? name : Enum.Format(underlying, Enum.Parse(underlying, name), "D");
            options.Add(new HalFormsOption(name, value));
        }

        return options.Count > 0 ? new HalFormsOptions(options) : null;
    }

    private static bool SerializesAsString(Type enumType)
        => enumType.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType is { } converter
            && (converter == typeof(JsonStringEnumConverter)
                || (converter.IsGenericType && converter.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>)));

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
