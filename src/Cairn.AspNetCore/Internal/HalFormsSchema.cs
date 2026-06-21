using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

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

        foreach (var property in input.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                properties.Add(BuildProperty(property));
            }
        }

        return properties;
    }

    private static HalFormsProperty BuildProperty(PropertyInfo property)
    {
        var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var range = property.GetCustomAttribute<RangeAttribute>();
        var display = property.GetCustomAttribute<DisplayAttribute>();

        return new HalFormsProperty(JsonNamingPolicy.CamelCase.ConvertName(property.Name))
        {
            Prompt = display?.GetName(),
            Required = property.GetCustomAttribute<RequiredAttribute>() is not null ? true : null,
            ReadOnly = IsReadOnly(property) ? true : null,
            Type = property.GetCustomAttribute<EmailAddressAttribute>() is not null ? "email" : MapType(underlying),
            Placeholder = display?.GetPrompt(),
            Regex = property.GetCustomAttribute<RegularExpressionAttribute>()?.Pattern,
            MaxLength = property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength ?? MaxLengthOf(property),
            Min = ToDouble(range?.Minimum),
            Max = ToDouble(range?.Maximum),
            Options = BuildOptions(underlying),
        };
    }

    private static bool IsReadOnly(PropertyInfo property)
        => property.GetCustomAttribute<EditableAttribute>() is { AllowEdit: false }
            || property.GetCustomAttribute<ReadOnlyAttribute>() is { IsReadOnly: true };

    // An enum-typed property becomes a fixed list of selectable values.
    private static HalFormsOptions? BuildOptions(Type underlying)
    {
        if (!underlying.IsEnum)
        {
            return null;
        }

        var options = new List<HalFormsOption>();
        foreach (var name in Enum.GetNames(underlying))
        {
            options.Add(new HalFormsOption(name, name));
        }

        return options.Count > 0 ? new HalFormsOptions(options) : null;
    }

    private static string? MapType(Type type)
    {
        if (type == typeof(bool))
        {
            return "checkbox";
        }

        if (type == typeof(DateOnly))
        {
            return "date";
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "datetime";
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
