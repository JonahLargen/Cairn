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
        id: "CAIRN003",
        title: "Route name collides in the generated catalog",
        messageFormat: "Route name '{0}' was not added to the Routes catalog because it maps to the same method name '{2}' as route '{1}'",
        category: "Cairn",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two route names that reduce to the same C# method name cannot both appear in the Routes catalog; rename one.");

    // The named-route attributes discovered via ForAttributeWithMetadataName. Matching by metadata name keeps
    // the provider incremental (the compiler pre-filters candidates) and stops look-alike attributes from
    // other namespaces feeding the catalog.
    private static readonly string[] RouteAttributeMetadataNames =
    [
        "Microsoft.AspNetCore.Mvc.RouteAttribute",
        "Microsoft.AspNetCore.Mvc.HttpGetAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPostAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPutAttribute",
        "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPatchAttribute",
        "Microsoft.AspNetCore.Mvc.HttpHeadAttribute",
        "Microsoft.AspNetCore.Mvc.HttpOptionsAttribute",
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Minimal-API endpoints: .WithName(name) in a Map* fluent chain.
        var fromEndpoints = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsWithNameInvocation(node),
                transform: static (syntaxContext, cancellationToken) => Extract(syntaxContext, cancellationToken))
            .Where(static route => route is not null)
            .Select(static (route, _) => route!.Value)
            .Collect();

        // Controllers: [HttpGet(Name = "name")] / [Route("...", Name = "name")] attributes.
        var fromControllers = NamedControllerRoutes(context);

        context.RegisterSourceOutput(
            fromEndpoints.Combine(fromControllers),
            static (production, pair) => Generate(production, pair.Left.AddRange(pair.Right)));
    }

    private static IncrementalValueProvider<ImmutableArray<RouteInfo>> NamedControllerRoutes(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<RouteInfo>>? combined = null;

        foreach (var metadataName in RouteAttributeMetadataNames)
        {
            var provider = context.SyntaxProvider.ForAttributeWithMetadataName(
                    metadataName,
                    predicate: static (node, _) => node is MethodDeclarationSyntax or TypeDeclarationSyntax,
                    transform: static (attributeContext, _) => ExtractFromAttributes(attributeContext))
                .SelectMany(static (routes, _) => routes)
                .Collect();

            combined = combined is { } existing
                ? existing.Combine(provider).Select(static (pair, _) => pair.Left.AddRange(pair.Right))
                : provider;
        }

        return combined!.Value;
    }

    private static bool IsWithNameInvocation(SyntaxNode node)
        => node is InvocationExpressionSyntax
        {
            ArgumentList.Arguments.Count: 1,
            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WithName" },
        };

    private static ImmutableArray<RouteInfo> ExtractFromAttributes(GeneratorAttributeSyntaxContext context)
    {
        // The controller's own [Route("prefix")] template, if any (a non-absolute action template hangs off it).
        var prefix = context.TargetSymbol is IMethodSymbol method ? ControllerRoutePrefix(method.ContainingType) : null;
        var builder = ImmutableArray.CreateBuilder<RouteInfo>();

        foreach (var attribute in context.Attributes)
        {
            if (NamedStringArgument(attribute, "Name") is not { } name)
            {
                continue;
            }

            var actionTemplate = FirstConstructorStringArgument(attribute) ?? string.Empty;
            builder.Add(new RouteInfo(name, ParseParameters(CombineController(prefix, actionTemplate))));
        }

        return builder.ToImmutable();
    }

    private static string? ControllerRoutePrefix(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "RouteAttribute")
            {
                return FirstConstructorStringArgument(attribute);
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

    // Attribute arguments arrive as compiler-evaluated constants, so a Name (or template) declared through a
    // const or nameof(...) resolves for free.
    private static string? NamedStringArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value is { Kind: TypedConstantKind.Primitive, Value: string value })
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstConstructorStringArgument(AttributeData attribute)
        => attribute.ConstructorArguments.Length > 0
            && attribute.ConstructorArguments[0] is { Kind: TypedConstantKind.Primitive, Value: string value }
            ? value
            : null;

    private static RouteInfo? Extract(GeneratorSyntaxContext context, System.Threading.CancellationToken cancellationToken)
    {
        var withName = (InvocationExpressionSyntax)context.Node;
        if (FirstStringArgument(context.SemanticModel, withName, cancellationToken) is not { } name)
        {
            return null;
        }

        var receiver = ((MemberAccessExpressionSyntax)withName.Expression).Expression;
        return FindRouteTemplate(context.SemanticModel, receiver, cancellationToken) is { } template
            ? new RouteInfo(name, ParseParameters(template))
            : null;
    }

    private static string? FindRouteTemplate(SemanticModel semanticModel, ExpressionSyntax expression, System.Threading.CancellationToken cancellationToken)
    {
        string? endpointTemplate = null;
        var prefixes = new List<string>();
        CollectChain(semanticModel, expression, prefixes, ref endpointTemplate, depth: 0, cancellationToken);

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

    // Walks the fluent chain outward from .WithName toward the app, collecting the endpoint's own template
    // plus any MapGroup("/prefix") prefixes so group route parameters are included. When the chain bottoms out
    // at a local variable (`var g = app.MapGroup(...); g.MapGet(...)`), continues from the variable's
    // initializer so group prefixes bound to variables are included too. Prefixes accumulate innermost-first.
    private static void CollectChain(SemanticModel semanticModel, ExpressionSyntax expression, List<string> prefixes, ref string? endpointTemplate, int depth, System.Threading.CancellationToken cancellationToken)
    {
        while (expression is InvocationExpressionSyntax invocation && invocation.Expression is MemberAccessExpressionSyntax member)
        {
            var method = member.Name.Identifier.ValueText;
            if (endpointTemplate is null && method is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" or "MapMethods")
            {
                endpointTemplate = FirstStringArgument(semanticModel, invocation, cancellationToken);
            }
            else if (method == "MapGroup" && FirstStringArgument(semanticModel, invocation, cancellationToken) is { } prefix)
            {
                prefixes.Add(prefix);
            }

            expression = member.Expression;
        }

        // Depth-bounded to defend against pathological (or cyclic) variable chains.
        if (depth < 8 && expression is IdentifierNameSyntax identifier && FindLocalInitializer(identifier) is { } initializer)
        {
            CollectChain(semanticModel, initializer, prefixes, ref endpointTemplate, depth + 1, cancellationToken);
        }
    }

    // The initializer of the nearest local variable declaration with the identifier's name, searching the
    // enclosing scopes outward (a block, a method/local function, or top-level statements).
    private static ExpressionSyntax? FindLocalInitializer(IdentifierNameSyntax identifier)
    {
        var name = identifier.Identifier.ValueText;
        for (SyntaxNode? scope = identifier.Parent; scope is not null; scope = scope.Parent)
        {
            if (scope is not (BlockSyntax or MethodDeclarationSyntax or LocalFunctionStatementSyntax or CompilationUnitSyntax))
            {
                continue;
            }

            foreach (var declarator in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Identifier.ValueText == name && declarator.Initializer is { } initializer)
                {
                    return initializer.Value;
                }
            }
        }

        return null;
    }

    private static void AppendSegment(StringBuilder builder, string segment)
    {
        var trimmed = segment.Trim('/');
        if (trimmed.Length > 0)
        {
            builder.Append('/').Append(trimmed);
        }
    }

    // A string literal, or any expression the compiler can evaluate to a constant string (a const field,
    // nameof(...), concatenated constants) — mirrors the RouteNameAnalyzer's resolution, so route names
    // factored into constants still surface in the catalog.
    private static string? FirstStringArgument(SemanticModel semanticModel, InvocationExpressionSyntax invocation, System.Threading.CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.FirstOrDefault() is not { } argument)
        {
            return null;
        }

        if (argument.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        var constant = semanticModel.GetConstantValue(argument.Expression, cancellationToken);
        return constant is { HasValue: true, Value: string value } ? value : null;
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

            // Both optional markers make the parameter optional for callers too: an inline default
            // ("{id=5}" / "{id:int=5}") or a trailing '?' ("{id?}" / "{id:int?}") — the route resolves
            // without the value either way.
            var optional = false;
            var equals = content.IndexOf('=');
            if (equals >= 0)
            {
                content = content.Substring(0, equals);
                optional = true;
            }
            else if (content.EndsWith("?", System.StringComparison.Ordinal))
            {
                content = content.Substring(0, content.Length - 1);
                optional = true;
            }

            var colon = content.IndexOf(':');
            string name;
            var type = "string";

            if (colon >= 0)
            {
                name = content.Substring(0, colon);
                type = MapConstraint(content.Substring(colon + 1).Split(':')[0].Split('(')[0]);
            }
            else
            {
                name = content;
            }
            if (IsIdentifier(name) && seen.Add(name))
            {
                parameters.Add(name + " " + type + (optional ? " ?" : string.Empty));
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

        // Numeric range constraints on an otherwise untyped parameter: ASP.NET evaluates them as long.
        "min" or "max" or "range" => "long",
        _ => "string",
    };

    private static void Generate(SourceProductionContext context, ImmutableArray<RouteInfo> routes)
    {
        // With zero named routes the class is emitted empty (rather than not at all) so user code that
        // references Cairn.Routes keeps compiling in a project that has no named endpoints yet.
        if (routes.IsDefault)
        {
            routes = ImmutableArray<RouteInfo>.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Cairn");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>Strongly-typed route references generated from named endpoints.</summary>");
        // internal: a route catalog is app-internal by nature, and an internal class can neither collide with
        // a hand-written public Cairn.Routes in a referencing project nor be ambiguous (CS0433) across
        // assemblies that each generate one. partial already allows same-assembly extension.
        builder.AppendLine("    internal static partial class Routes");
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

            // Optional/defaulted route parameters are optional for callers too: nullable with a null default,
            // and skipped in the route values when omitted so link generation renders the URL without them.
            var signature = string.Join(", ", parameters.Select(static p
                => p.Optional ? p.Type + "? " + Escape(p.Name) + " = null" : p.Type + " " + Escape(p.Name)));

            builder.AppendLine($"        /// <summary>Route to the '{XmlEscape(route.Name)}' endpoint.</summary>");
            var name = SymbolDisplay.FormatLiteral(route.Name, quote: true);
            if (parameters.Exists(static p => p.Optional))
            {
                // The local must not shadow a same-named route parameter (CS0136 in the generated catalog),
                // so start from an unlikely name and suffix-rename while one still claims it.
                var local = "__cairnRouteValues";
                while (parameters.Exists(p => string.Equals(p.Name, local, StringComparison.Ordinal)))
                {
                    local += "_";
                }

                builder.AppendLine($"        public static global::Cairn.LinkTarget {method}({signature})");
                builder.AppendLine("        {");
                builder.AppendLine($"            var {local} = new global::System.Collections.Generic.Dictionary<string, object?>();");
                foreach (var parameter in parameters)
                {
                    var escaped = Escape(parameter.Name);
                    if (parameter.Optional)
                    {
                        builder.AppendLine($"            if ({escaped} is not null)");
                        builder.AppendLine("            {");
                        builder.AppendLine($"                {local}[{SymbolDisplay.FormatLiteral(parameter.Name, quote: true)}] = {escaped};");
                        builder.AppendLine("            }");
                        builder.AppendLine();
                    }
                    else
                    {
                        builder.AppendLine($"            {local}[{SymbolDisplay.FormatLiteral(parameter.Name, quote: true)}] = {escaped};");
                    }
                }

                builder.AppendLine($"            return global::Cairn.LinkTarget.Route({name}, {local});");
                builder.AppendLine("        }");
            }
            else
            {
                var values = parameters.Count == 0
                    ? "null"
                    : "new { " + string.Join(", ", parameters.Select(static p => Escape(p.Name))) + " }";
                builder.AppendLine($"        public static global::Cairn.LinkTarget {method}({signature})");
                builder.AppendLine($"            => global::Cairn.LinkTarget.Route({name}, {values});");
            }

            builder.AppendLine();
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("Cairn.Routes.g.cs", builder.ToString());
    }

    private static List<(string Name, string Type, bool Optional)> Decode(string encoded)
        => encoded.Length == 0
            ? new List<(string, string, bool)>()
            : encoded.Split('|').Select(static part => part.Split(' ')).Select(static pieces => (pieces[0], pieces[1], pieces.Length > 2)).ToList();

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

    private static string XmlEscape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Escape(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;

    private static bool IsIdentifier(string name)
        => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(static c => char.IsLetterOrDigit(c) || c == '_');

    private readonly record struct RouteInfo(string Name, string Parameters);
}
