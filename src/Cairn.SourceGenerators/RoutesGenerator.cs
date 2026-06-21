using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cairn.SourceGenerators;

/// <summary>Generates a strongly-typed <c>Cairn.Routes</c> catalog from named minimal-API endpoints.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class RoutesGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor RouteNameCollision = new(
        id: "CAIRN002",
        title: "Route name collides in the generated catalog",
        messageFormat: "Route name '{0}' was not added to the Routes catalog because it maps to the same method name '{2}' as route '{1}'",
        category: "Cairn",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two route names that reduce to the same C# method name cannot both appear in the Routes catalog; rename one.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Minimal-API endpoints: .WithName("name") in a Map* fluent chain.
        var fromEndpoints = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsWithNameInvocation(node),
                transform: static (syntaxContext, _) => Extract((InvocationExpressionSyntax)syntaxContext.Node))
            .Where(static route => route is not null)
            .Select(static (route, _) => route!.Value)
            .Collect();

        // Controllers: [HttpGet(Name = "name")] / [Route("...", Name = "name")] attributes.
        var fromControllers = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsNamedRouteAttribute(node),
                transform: static (syntaxContext, _) => ExtractFromAttribute((AttributeSyntax)syntaxContext.Node))
            .Where(static route => route is not null)
            .Select(static (route, _) => route!.Value)
            .Collect();

        context.RegisterSourceOutput(
            fromEndpoints.Combine(fromControllers),
            static (production, pair) => Generate(production, pair.Left.AddRange(pair.Right)));
    }

    private static bool IsWithNameInvocation(SyntaxNode node)
        => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WithName" } };

    private static bool IsNamedRouteAttribute(SyntaxNode node)
        => node is AttributeSyntax attribute
            && IsRouteAttribute(SimpleName(attribute.Name))
            && attribute.ArgumentList is { } arguments
            && arguments.Arguments.Any(static argument => argument.NameEquals?.Name.Identifier.ValueText == "Name");

    private static RouteInfo? ExtractFromAttribute(AttributeSyntax attribute)
    {
        if (NamedArgument(attribute, "Name") is not { } name)
        {
            return null;
        }

        var actionTemplate = FirstAttributeStringArgument(attribute) ?? string.Empty;
        var prefix = attribute.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not null ? ControllerRoutePrefix(attribute) : null;
        return new RouteInfo(name, ParseParameters(CombineController(prefix, actionTemplate)));
    }

    // The controller's own [Route("prefix")] template, if any (a non-absolute action template hangs off it).
    private static string? ControllerRoutePrefix(AttributeSyntax attribute)
    {
        if (attribute.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } type)
        {
            return null;
        }

        foreach (var list in type.AttributeLists)
        {
            foreach (var candidate in list.Attributes)
            {
                if (SimpleName(candidate.Name) is "Route" or "RouteAttribute")
                {
                    return FirstAttributeStringArgument(candidate);
                }
            }
        }

        return null;
    }

    private static string CombineController(string? prefix, string actionTemplate)
    {
        // A '~/'-rooted action template is application-root-relative and overrides the controller prefix.
        if (actionTemplate.StartsWith("~/", System.StringComparison.Ordinal))
        {
            return actionTemplate.Substring(1);
        }

        // An action template that starts with '/' is absolute and overrides the controller prefix.
        if (actionTemplate.StartsWith("/", System.StringComparison.Ordinal))
        {
            return actionTemplate;
        }

        var builder = new StringBuilder();
        AppendSegment(builder, prefix ?? string.Empty);
        AppendSegment(builder, actionTemplate);
        return builder.ToString();
    }

    private static string? NamedArgument(AttributeSyntax attribute, string name)
    {
        if (attribute.ArgumentList is not { } arguments)
        {
            return null;
        }

        foreach (var argument in arguments.Arguments)
        {
            if (argument.NameEquals?.Name.Identifier.ValueText == name
                && argument.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText;
            }
        }

        return null;
    }

    private static string? FirstAttributeStringArgument(AttributeSyntax attribute)
    {
        if (attribute.ArgumentList is not { } arguments)
        {
            return null;
        }

        foreach (var argument in arguments.Arguments)
        {
            if (argument.NameEquals is null && argument.NameColon is null
                && argument.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText;
            }
        }

        return null;
    }

    private static string SimpleName(NameSyntax name) => name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        _ => name.ToString(),
    };

    private static bool IsRouteAttribute(string name)
    {
        if (name.EndsWith("Attribute", System.StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - "Attribute".Length);
        }

        return name is "Route" or "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch" or "HttpHead" or "HttpOptions";
    }

    private static RouteInfo? Extract(InvocationExpressionSyntax withName)
    {
        if (FirstStringLiteral(withName) is not { } name)
        {
            return null;
        }

        var receiver = ((MemberAccessExpressionSyntax)withName.Expression).Expression;
        return FindRouteTemplate(receiver) is { } template ? new RouteInfo(name, ParseParameters(template)) : null;
    }

    private static string? FindRouteTemplate(ExpressionSyntax expression)
    {
        string? endpointTemplate = null;
        var prefixes = new List<string>();

        // Walk the fluent chain outward from .WithName toward the app, collecting the endpoint's own template
        // plus any inline MapGroup("/prefix") prefixes so group route parameters are included. (Group prefixes
        // bound to a local variable, e.g. `var g = app.MapGroup(...); g.MapGet(...)`, are not visible here.)
        while (expression is InvocationExpressionSyntax invocation && invocation.Expression is MemberAccessExpressionSyntax member)
        {
            var method = member.Name.Identifier.ValueText;
            if (endpointTemplate is null && method is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" or "MapMethods")
            {
                endpointTemplate = FirstStringLiteral(invocation);
            }
            else if (method == "MapGroup" && FirstStringLiteral(invocation) is { } prefix)
            {
                prefixes.Add(prefix);
            }

            expression = member.Expression;
        }

        if (endpointTemplate is null)
        {
            return null;
        }

        if (prefixes.Count == 0)
        {
            return endpointTemplate;
        }

        // prefixes were collected innermost-first; emit outermost-first, then the endpoint template.
        var builder = new StringBuilder();
        for (var i = prefixes.Count - 1; i >= 0; i--)
        {
            AppendSegment(builder, prefixes[i]);
        }

        AppendSegment(builder, endpointTemplate);
        return builder.ToString();
    }

    private static void AppendSegment(StringBuilder builder, string segment)
    {
        var trimmed = segment.Trim('/');
        if (trimmed.Length > 0)
        {
            builder.Append('/').Append(trimmed);
        }
    }

    private static string? FirstStringLiteral(InvocationExpressionSyntax invocation)
    {
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault();
        return argument?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;
    }

    // Encodes the route's parameters as "name:type|name2:type2".
    private static string ParseParameters(string template)
    {
        var parameters = new List<string>();
        // Route parameter names are case-insensitive and unique; a repeat (e.g. a group prefix and an endpoint
        // both using {id}) must not produce a duplicate C# parameter — keep the first occurrence.
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < template.Length)
        {
            if (template[index] != '{')
            {
                index++;
                continue;
            }

            var end = template.IndexOf('}', index);
            if (end < 0)
            {
                break;
            }

            var content = template.Substring(index + 1, end - index - 1).TrimStart('*');

            // Strip an inline default value: "{id=5}" / "{id:int=5}".
            var equals = content.IndexOf('=');
            if (equals >= 0)
            {
                content = content.Substring(0, equals);
            }

            var colon = content.IndexOf(':');
            string name;
            var type = "string";

            if (colon >= 0)
            {
                name = content.Substring(0, colon);
                // TrimEnd('?') so an optional constraint like "{id:int?}" maps to int, not the default string.
                type = MapConstraint(content.Substring(colon + 1).Split(':')[0].Split('(')[0].TrimEnd('?'));
            }
            else
            {
                name = content;
            }

            name = name.TrimEnd('?');
            if (IsIdentifier(name) && seen.Add(name))
            {
                parameters.Add(name + " " + type);
            }

            index = end + 1;
        }

        return string.Join("|", parameters);
    }

    private static string MapConstraint(string constraint) => constraint switch
    {
        "int" => "int",
        "long" => "long",
        "bool" => "bool",
        "double" => "double",
        "float" => "float",
        "decimal" => "decimal",
        "guid" => "global::System.Guid",
        "datetime" => "global::System.DateTime",
        _ => "string",
    };

    private static void Generate(SourceProductionContext context, ImmutableArray<RouteInfo> routes)
    {
        if (routes.IsDefaultOrEmpty)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Cairn");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>Strongly-typed route references generated from named endpoints.</summary>");
        builder.AppendLine("    public static partial class Routes");
        builder.AppendLine("    {");

        var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            var method = Sanitize(route.Name);
            if (method.Length == 0)
            {
                continue;
            }

            if (emitted.TryGetValue(method, out var winner))
            {
                // A distinct route name that reduces to an already-emitted method is dropped; warn so the loss is visible.
                if (!string.Equals(winner, route.Name, StringComparison.Ordinal))
                {
                    context.ReportDiagnostic(Diagnostic.Create(RouteNameCollision, Location.None, route.Name, winner, method));
                }

                continue;
            }

            emitted[method] = route.Name;

            var parameters = Decode(route.Parameters);
            var signature = string.Join(", ", parameters.Select(static p => p.Type + " " + Escape(p.Name)));
            var values = parameters.Count == 0
                ? "null"
                : "new { " + string.Join(", ", parameters.Select(static p => Escape(p.Name))) + " }";

            builder.AppendLine($"        /// <summary>Route to the '{route.Name}' endpoint.</summary>");
            builder.AppendLine($"        public static global::Cairn.LinkTarget {method}({signature})");
            builder.AppendLine($"            => global::Cairn.LinkTarget.Route(\"{route.Name}\", {values});");
            builder.AppendLine();
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("Cairn.Routes.g.cs", builder.ToString());
    }

    private static List<(string Name, string Type)> Decode(string encoded)
        => encoded.Length == 0
            ? new List<(string, string)>()
            : encoded.Split('|').Select(static part => part.Split(' ')).Select(static pieces => (pieces[0], pieces[1])).ToList();

    private static string Sanitize(string routeName)
    {
        var builder = new StringBuilder();
        var capitalizeNext = true;

        foreach (var c in routeName)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }
            else
            {
                capitalizeNext = true;
            }
        }

        var result = builder.ToString();
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static string Escape(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;

    private static bool IsIdentifier(string name)
        => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(static c => char.IsLetterOrDigit(c) || c == '_');

    private readonly record struct RouteInfo(string Name, string Parameters);
}
