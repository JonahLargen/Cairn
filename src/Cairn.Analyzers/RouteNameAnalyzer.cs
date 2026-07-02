using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers;

/// <summary>
/// Flags <c>LinkTarget.Route("name")</c> calls whose route name is not declared by any
/// <c>.WithName("name")</c> endpoint in the compilation.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RouteNameAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "CAIRN001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Link route name does not match any endpoint",
        messageFormat: "No endpoint or controller route is named '{0}'{1}, so the link will not resolve at runtime",
        category: "Cairn",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A LinkTarget.Route name should match an endpoint named with WithName or a controller route attribute such as [HttpGet(Name = \"...\")]. When the compilation declares no named endpoints at all, the rule stays silent (the routes are assumed to live in another project); names declared elsewhere can also be listed via cairn_additional_route_names in .editorconfig or the CairnAdditionalRouteNames MSBuild property.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var routeNames = new ConcurrentDictionary<string, byte>();
        var references = new ConcurrentBag<(string Name, Location Location)>();

        context.RegisterSyntaxNodeAction(
            nodeContext => Collect(nodeContext, routeNames, references),
            SyntaxKind.InvocationExpression);

        context.RegisterSyntaxNodeAction(
            nodeContext => CollectAttribute(nodeContext, routeNames),
            SyntaxKind.Attribute);

        context.RegisterCompilationEndAction(endContext => Report(endContext, routeNames, references));
    }

    private static void Collect(
        SyntaxNodeAnalysisContext context,
        ConcurrentDictionary<string, byte> routeNames,
        ConcurrentBag<(string, Location)> references)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // The method's simple name is a cheap syntactic pre-filter; only "Route" calls pay for symbol lookup.
        // An IdentifierName expression covers `using static Cairn.LinkTarget` call sites.
        var method = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };

        if (method == "WithName")
        {
            if (FirstStringArgument(context, invocation) is { } endpoint)
            {
                routeNames.TryAdd(endpoint.Value, 0);
            }
        }
        else if (method == "Route" && IsLinkTargetRoute(context, invocation))
        {
            if (FirstStringArgument(context, invocation) is { } route)
            {
                references.Add((route.Value, route.Location));
            }
        }
    }

    // Collect named controller routes: [HttpGet(Name = "...")], [Route("...", Name = "...")], etc.
    // Name = arguments resolve through GetConstantValue — exactly as FirstStringArgument does for WithName,
    // and mirroring the generator's TypedConstant resolution — so a name declared via nameof(...) or a const
    // is collected instead of false-positiving every reference to it.
    private static void CollectAttribute(SyntaxNodeAnalysisContext context, ConcurrentDictionary<string, byte> routeNames)
    {
        var attribute = (AttributeSyntax)context.Node;
        if (!IsRouteAttribute(SimpleName(attribute.Name)) || attribute.ArgumentList is not { } arguments)
        {
            return;
        }

        foreach (var argument in arguments.Arguments)
        {
            if (argument.NameEquals?.Name.Identifier.ValueText != "Name")
            {
                continue;
            }

            if (argument.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                routeNames.TryAdd(literal.Token.ValueText, 0);
            }
            else if (context.SemanticModel.GetConstantValue(argument.Expression, context.CancellationToken) is { HasValue: true, Value: string name })
            {
                routeNames.TryAdd(name, 0);
            }
        }
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

    private static void Report(
        CompilationAnalysisContext context,
        ConcurrentDictionary<string, byte> routeNames,
        ConcurrentBag<(string Name, Location Location)> references)
    {
        // No endpoints are named in this compilation, so route names are likely defined elsewhere.
        if (routeNames.IsEmpty)
        {
            return;
        }

        // Names declared in other projects can be listed in .editorconfig / a global analyzer config
        // (cairn_additional_route_names = a, b) or via an MSBuild CompilerVisibleProperty
        // (CairnAdditionalRouteNames), so multi-project solutions can silence cross-project references.
        foreach (var configured in AdditionalRouteNames(context))
        {
            routeNames.TryAdd(configured, 0);
        }

        foreach (var (name, location) in references)
        {
            if (routeNames.ContainsKey(name))
            {
                continue;
            }

            var suggestion = ClosestMatch(name, routeNames.Keys);
            var properties = suggestion is null
                ? ImmutableDictionary<string, string?>.Empty
                : ImmutableDictionary<string, string?>.Empty.Add("suggestion", suggestion);
            var hint = suggestion is null ? string.Empty : $" (did you mean '{suggestion}'?)";

            context.ReportDiagnostic(Diagnostic.Create(Rule, location, properties, name, hint));
        }
    }

    private static IEnumerable<string> AdditionalRouteNames(CompilationAnalysisContext context)
    {
        var provider = context.Options.AnalyzerConfigOptionsProvider;
        var raw = new List<string>();

        if (provider.GlobalOptions.TryGetValue("cairn_additional_route_names", out var global))
        {
            raw.Add(global);
        }

        if (provider.GlobalOptions.TryGetValue("build_property.CairnAdditionalRouteNames", out var property))
        {
            raw.Add(property);
        }

        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            if (provider.GetOptions(tree).TryGetValue("cairn_additional_route_names", out var perTree))
            {
                raw.Add(perTree);
            }
        }

        foreach (var list in raw)
        {
            foreach (var name in list.Split(','))
            {
                var trimmed = name.Trim();
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }
    }

    // The invocation must bind to a method on Cairn.LinkTarget (checked by metadata name via the semantic
    // model): a look-alike LinkTarget in another namespace is not a Cairn link reference, while `using static
    // Cairn.LinkTarget` and type aliases — invisible to a syntax check — are.
    private static bool IsLinkTargetRoute(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        var info = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);

        return symbol is IMethodSymbol method
            && method.ContainingType is { Name: "LinkTarget", ContainingType: null } type
            && type.ContainingNamespace is { Name: "Cairn", ContainingNamespace.IsGlobalNamespace: true };
    }

    // A string literal, or any expression the compiler can evaluate to a constant string (a const field,
    // nameof(...), concatenated constants) — so names factored into constants neither false-positive as
    // undeclared nor escape validation as references.
    private static (string Value, Location Location)? FirstStringArgument(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.FirstOrDefault() is not { } argument)
        {
            return null;
        }

        if (argument.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return (literal.Token.ValueText, literal.GetLocation());
        }

        var constant = context.SemanticModel.GetConstantValue(argument.Expression, context.CancellationToken);
        return constant is { HasValue: true, Value: string name }
            ? (name, argument.Expression.GetLocation())
            : null;
    }

    private static string? ClosestMatch(string value, ICollection<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = Levenshtein(value, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= 2 ? best : null;
    }

    private static int Levenshtein(string source, string target)
    {
        var distances = new int[source.Length + 1, target.Length + 1];

        for (var i = 0; i <= source.Length; i++)
        {
            distances[i, 0] = i;
        }

        for (var j = 0; j <= target.Length; j++)
        {
            distances[0, j] = j;
        }

        for (var i = 1; i <= source.Length; i++)
        {
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                distances[i, j] = System.Math.Min(
                    System.Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[source.Length, target.Length];
    }
}
