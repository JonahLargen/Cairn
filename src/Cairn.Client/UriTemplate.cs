using System.Collections;
using System.Globalization;
using System.Text;

namespace Cairn.Client;

/// <summary>
/// Expands RFC 6570 URI templates (levels 1-4): simple <c>{var}</c>, reserved <c>{+var}</c>, fragment
/// <c>{#var}</c>, label <c>{.var}</c>, path <c>{/var}</c>, path-style <c>{;var}</c>, query <c>{?var,var2}</c>,
/// query-continuation <c>{&amp;var}</c>, prefix modifiers <c>{var:3}</c>, and explode <c>{list*}</c> over
/// lists and associative maps. Undefined variables (and empty lists/maps) are dropped.
/// </summary>
internal static class UriTemplate
{
    public static string Expand(string template, object? variables)
    {
        if (template.IndexOf('{') < 0)
        {
            return template;
        }

        var vars = ToVariables(variables);
        var result = new StringBuilder(template.Length);
        var index = 0;

        while (index < template.Length)
        {
            var c = template[index];
            if (c != '{')
            {
                result.Append(c);
                index++;
                continue;
            }

            var end = template.IndexOf('}', index);
            if (end < 0)
            {
                result.Append(template, index, template.Length - index);
                break;
            }

            ExpandExpression(result, template.Substring(index + 1, end - index - 1), vars);
            index = end + 1;
        }

        return result.ToString();
    }

    private static void ExpandExpression(StringBuilder result, string expression, IReadOnlyDictionary<string, object?> vars)
    {
        if (expression.Length == 0)
        {
            return;
        }

        var op = expression[0] is '+' or '#' or '.' or '/' or ';' or '?' or '&' ? expression[0] : '\0';
        var names = (op == '\0' ? expression : expression[1..]).Split(',');
        var (prefix, separator, named, ifEmpty, allowReserved) = OperatorFor(op);

        var any = false;
        foreach (var rawName in names)
        {
            // Parse the modifiers: a prefix length (":n") or explode ("*").
            var name = rawName;
            var prefixLength = -1;
            var explode = false;

            var colon = name.IndexOf(':');
            if (colon >= 0)
            {
                if (int.TryParse(name[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    prefixLength = parsed;
                }

                name = name[..colon];
            }
            else if (name.EndsWith('*'))
            {
                explode = true;
                name = name[..^1];
            }

            if (!vars.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            switch (Classify(value))
            {
                case VariableKind.Scalar:
                    var text = Stringify(value)!;
                    if (prefixLength >= 0)
                    {
                        text = TakeCodePoints(text, prefixLength);
                    }

                    result.Append(any ? separator : prefix);
                    any = true;
                    AppendNamedValue(result, name, text, named, ifEmpty, allowReserved);
                    break;

                case VariableKind.List:
                    any |= AppendList(result, name, (IEnumerable)value, any ? separator : prefix, separator, named, ifEmpty, allowReserved, explode);
                    break;

                case VariableKind.Map:
                    any |= AppendMap(result, name, value, any ? separator : prefix, separator, named, ifEmpty, allowReserved, explode);
                    break;
            }
        }
    }

    private static void AppendNamedValue(StringBuilder result, string name, string value, bool named, string ifEmpty, bool allowReserved)
    {
        if (named)
        {
            result.Append(Encode(name, allowReserved: true));
            if (value.Length == 0)
            {
                result.Append(ifEmpty);
                return;
            }

            result.Append('=');
        }

        result.Append(Encode(value, allowReserved));
    }

    // {list} -> v1,v2 (named: name=v1,v2); {list*} -> v1<sep>v2 (named: name=v1<sep>name=v2).
    // Returns whether anything was emitted: an empty list is treated as undefined per RFC 6570.
    private static bool AppendList(StringBuilder result, string name, IEnumerable items, string first, string separator, bool named, string ifEmpty, bool allowReserved, bool explode)
    {
        var any = false;
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            var text = Stringify(item)!;
            if (!any)
            {
                result.Append(first);
                if (!explode && named)
                {
                    result.Append(Encode(name, allowReserved: true)).Append('=');
                }
            }
            else
            {
                result.Append(explode ? separator : ",");
            }

            any = true;
            if (explode && named)
            {
                AppendNamedValue(result, name, text, named: true, ifEmpty, allowReserved);
            }
            else
            {
                result.Append(Encode(text, allowReserved));
            }
        }

        return any;
    }

    // {map} -> k1,v1,k2,v2 (named: name=k1,v1,k2,v2); {map*} -> k1=v1<sep>k2=v2.
    private static bool AppendMap(StringBuilder result, string name, object map, string first, string separator, bool named, string ifEmpty, bool allowReserved, bool explode)
    {
        var any = false;
        foreach (var (key, value) in Pairs(map))
        {
            if (value is null)
            {
                continue;
            }

            var text = Stringify(value)!;
            if (!any)
            {
                result.Append(first);
                if (!explode && named)
                {
                    result.Append(Encode(name, allowReserved: true)).Append('=');
                }
            }
            else
            {
                result.Append(explode ? separator : ",");
            }

            any = true;
            if (explode)
            {
                result.Append(Encode(key, allowReserved)).Append('=').Append(Encode(text, allowReserved));
            }
            else
            {
                result.Append(Encode(key, allowReserved)).Append(',').Append(Encode(text, allowReserved));
            }
        }

        return any;
    }

    private static (string Prefix, string Separator, bool Named, string IfEmpty, bool AllowReserved) OperatorFor(char op) => op switch
    {
        '+' => (string.Empty, ",", false, string.Empty, true),
        '#' => ("#", ",", false, string.Empty, true),
        '.' => (".", ".", false, string.Empty, false),
        '/' => ("/", "/", false, string.Empty, false),
        ';' => (";", ";", true, string.Empty, false),
        '?' => ("?", "&", true, "=", false),
        '&' => ("&", "&", true, "=", false),
        _ => (string.Empty, ",", false, string.Empty, false),
    };

    private static string Encode(string value, bool allowReserved)
    {
        if (!allowReserved)
        {
            return Uri.EscapeDataString(value);
        }

        // Reserved expansion: leave unreserved + reserved (gen-delims/sub-delims) intact. A '%' passes through
        // only as part of a valid pct-triplet (RFC 6570 §3.2.3); a bare '%' is data and encodes as %25 — else
        // "50% off" would expand to the invalid URI "50%%20off". Iterate by code point, not UTF-16 unit:
        // encoding each half of a surrogate pair separately would yield U+FFFD replacement bytes (%EF%BF%BD)
        // and corrupt astral characters such as emoji.
        var builder = new StringBuilder(value.Length);
        Span<byte> utf8 = stackalloc byte[4];
        var index = 0;
        while (index < value.Length)
        {
            if (value[index] == '%')
            {
                builder.Append(index + 2 < value.Length && char.IsAsciiHexDigit(value[index + 1]) && char.IsAsciiHexDigit(value[index + 2])
                    ? "%"
                    : "%25");
                index++;
                continue;
            }

            // A lone surrogate has no valid code point; encode it as U+FFFD, matching EnumerateRunes.
            if (!Rune.TryGetRuneAt(value, index, out var rune))
            {
                rune = Rune.ReplacementChar;
            }

            if (rune.IsAscii && (IsUnreserved((char)rune.Value) || IsReserved((char)rune.Value)))
            {
                builder.Append((char)rune.Value);
            }
            else
            {
                var length = rune.EncodeToUtf8(utf8);
                for (var i = 0; i < length; i++)
                {
                    builder.Append('%').Append(utf8[i].ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            // A lone surrogate occupies one UTF-16 unit, matching the replacement rune's sequence length.
            index += rune.Utf16SequenceLength;
        }

        return builder.ToString();
    }

    // The prefix modifier {var:n} takes the first n characters of the value (RFC 6570 §2.4.1) — code points,
    // not UTF-16 units, so a surrogate pair (e.g. an emoji) is never split into replacement bytes.
    private static string TakeCodePoints(string text, int count)
    {
        var index = 0;
        var taken = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken == count)
            {
                break;
            }

            index += rune.Utf16SequenceLength;
            taken++;
        }

        return index < text.Length ? text[..index] : text;
    }

    private static bool IsUnreserved(char c)
        => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~';

    private static bool IsReserved(char c)
        => c is ':' or '/' or '?' or '#' or '[' or ']' or '@' or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=';

    private enum VariableKind
    {
        Scalar,
        List,
        Map,
    }

    // A string is a scalar; a dictionary is an associative map; any other enumerable is a list.
    private static VariableKind Classify(object value) => value switch
    {
        string => VariableKind.Scalar,
        IDictionary => VariableKind.Map,
        IEnumerable enumerable when IsGenericPairSequence(enumerable) => VariableKind.Map,
        IEnumerable => VariableKind.List,
        _ => VariableKind.Scalar,
    };

    private static bool IsGenericPairSequence(IEnumerable value)
    {
        foreach (var iface in value.GetType().GetInterfaces())
        {
            if (iface.IsGenericType
                && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && iface.GetGenericArguments()[0] is { IsGenericType: true } element
                && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                && element.GetGenericArguments()[0] == typeof(string))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string Key, object? Value)> Pairs(object map)
    {
        if (map is IDictionary legacy)
        {
            foreach (DictionaryEntry entry in legacy)
            {
                if (Stringify(entry.Key) is { } key)
                {
                    yield return (key, entry.Value);
                }
            }

            yield break;
        }

        // A generic pair sequence without non-generic IDictionary (e.g. FrozenDictionary): reflect on Key/Value.
        foreach (var pair in (IEnumerable)map)
        {
            if (pair is null)
            {
                continue;
            }

            var type = pair.GetType();
            if (type.GetProperty("Key")?.GetValue(pair) is string key)
            {
                yield return (key, type.GetProperty("Value")?.GetValue(pair));
            }
        }
    }

    // Wire-shaped scalars, not .NET display strings: lowercase bools and round-trip ("O") date/time
    // values expand to something a server can parse back.
    private static string? Stringify(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static IReadOnlyDictionary<string, object?> ToVariables(object? variables)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables is null)
        {
            return dictionary;
        }

        // Any dictionary keyed by string supplies variables directly — Dictionary<string,string>,
        // Dictionary<string,object>, read-only and immutable variants alike.
        if (variables is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var (key, value) in map)
            {
                dictionary[key] = value;
            }

            return dictionary;
        }

        if (variables is IDictionary legacy)
        {
            foreach (DictionaryEntry entry in legacy)
            {
                if (entry.Key is not null && Stringify(entry.Key) is { } key)
                {
                    dictionary[key] = entry.Value;
                }
            }

            return dictionary;
        }

        if (variables is IEnumerable enumerable and not string && IsGenericPairSequence(enumerable))
        {
            foreach (var (key, value) in Pairs(enumerable))
            {
                dictionary[key] = value;
            }

            return dictionary;
        }

        foreach (var property in variables.GetType().GetProperties())
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                dictionary[property.Name] = property.GetValue(variables);
            }
        }

        return dictionary;
    }
}
