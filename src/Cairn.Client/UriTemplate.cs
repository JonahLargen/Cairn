using System.Globalization;
using System.Text;

namespace Cairn.Client;

/// <summary>
/// Expands RFC 6570 URI templates (levels 1-3): simple <c>{var}</c>, reserved <c>{+var}</c>, fragment
/// <c>{#var}</c>, label <c>{.var}</c>, path <c>{/var}</c>, path-style <c>{;var}</c>, query <c>{?var,var2}</c>,
/// and query-continuation <c>{&amp;var}</c>. Undefined variables are dropped. List/explode (level 4) is not handled.
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

    private static void ExpandExpression(StringBuilder result, string expression, IReadOnlyDictionary<string, string?> vars)
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
            // Strip a prefix modifier (":n") or explode ("*") — neither is expanded here.
            var name = rawName;
            var colon = name.IndexOf(':');
            if (colon >= 0)
            {
                name = name[..colon];
            }

            name = name.TrimEnd('*');

            if (!vars.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            result.Append(any ? separator : prefix);
            any = true;

            if (named)
            {
                result.Append(name);
                if (value.Length == 0)
                {
                    result.Append(ifEmpty);
                    continue;
                }

                result.Append('=');
            }

            result.Append(Encode(value, allowReserved));
        }
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

        // Reserved expansion: leave unreserved + reserved (gen-delims/sub-delims) and existing %-escapes intact.
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsUnreserved(ch) || IsReserved(ch))
            {
                builder.Append(ch);
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes([ch]))
                {
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char c)
        => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~';

    private static bool IsReserved(char c)
        => c is ':' or '/' or '?' or '#' or '[' or ']' or '@' or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or '%';

    private static IReadOnlyDictionary<string, string?> ToVariables(object? variables)
    {
        var dictionary = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (variables is null)
        {
            return dictionary;
        }

        if (variables is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var (key, value) in map)
            {
                dictionary[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            return dictionary;
        }

        foreach (var property in variables.GetType().GetProperties())
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                dictionary[property.Name] = Convert.ToString(property.GetValue(variables), CultureInfo.InvariantCulture);
            }
        }

        return dictionary;
    }
}
