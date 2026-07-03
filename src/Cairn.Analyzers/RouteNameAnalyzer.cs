using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers;

/// <summary>
/// Flags <c>LinkTarget.Route("name")</c> / <c>LinkTarget.RouteTemplate("name")</c> calls whose route name
/// is not declared by any named endpoint or controller route in the compilation.
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
        description: "A LinkTarget.Route or LinkTarget.RouteTemplate name should match an endpoint named with WithName, a conventional route (MapControllerRoute / MapAreaControllerRoute), or a controller route attribute such as [HttpGet(Name = \"...\")]. When the compilation declares no named endpoints at all, the rule stays silent (the routes are assumed to live in another project); names declared elsewhere can also be listed via cairn_additional_route_names in .editorconfig or the CairnAdditionalRouteNames MSBuild property.",
        helpLinkUri: "https://jonahlargen.github.io/Cairn/articles/route-safety.html#cairn001-unknown-route-name");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    // Diagnostics are reported straight from the node action (not a compilation-end action): the IDE only
    // offers a code fix's lightbulb for node-reported diagnostics. The declared-route-name index is built
    // once per compilation — lazily, on the first LinkTarget.Route reference — by walking every tree.
    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var options = context.Options;
        var cancellationToken = context.CancellationToken;
        var index = new Lazy<ImmutableHashSet<string>>(() => BuildRouteNameIndex(compilation, options, cancellationToken));

        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeInvocation(nodeContext, index),
            SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, Lazy<ImmutableHashSet<string>> index)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // The method's simple name is a cheap syntactic pre-filter; only Route/RouteTemplate calls pay for
        // symbol lookup. An IdentifierName expression covers `using static Cairn.LinkTarget` call sites.
        if (SimpleInvocationName(invocation) is not ("Route" or "RouteTemplate")
            || LinkTargetMethod(context.SemanticModel, invocation, context.CancellationToken) is not { } method
            || StringArgument(context.SemanticModel, invocation, method, context.CancellationToken) is not { } route)
        {
            return;
        }

        var routeNames = index.Value;

        // An empty index means no endpoints are named in this compilation, so route names are likely
        // defined elsewhere — stay silent rather than flag every reference.
        if (routeNames.IsEmpty || routeNames.Contains(route.Value))
        {
            return;
        }

        var suggestion = ClosestMatch(route.Value, routeNames);
        var properties = suggestion is null
            ? ImmutableDictionary<string, string?>.Empty
            : ImmutableDictionary<string, string?>.Empty.Add("suggestion", suggestion);
        var hint = suggestion is null ? string.Empty : $" (did you mean '{suggestion}'?)";

        context.ReportDiagnostic(Diagnostic.Create(Rule, route.Location, properties, route.Value, hint));
    }

    private static string? SimpleInvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    // Every route name the compilation declares: .WithName(...) endpoints, conventional routes
    // (MapControllerRoute / MapAreaControllerRoute), and named controller route attributes — plus any
    // names configured for cross-project references. Walking all trees once here costs the same work the
    // old collect-then-report-at-compilation-end shape did; it just finishes before the first report.
    private static ImmutableHashSet<string> BuildRouteNameIndex(Compilation compilation, AnalyzerOptions options, CancellationToken cancellationToken)
    {
        var declared = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The index is built once per compilation (this method sits behind a Lazy), so the per-tree
            // model costs the same binding work the old compilation-end shape paid — it just runs before
            // the first report instead of after the last collection.
#pragma warning disable RS1030 // Do not invoke Compilation.GetSemanticModel() within a diagnostic analyzer
            var semanticModel = compilation.GetSemanticModel(tree);
#pragma warning restore RS1030

            foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
            {
                if (node is InvocationExpressionSyntax invocation)
                {
                    CollectInvocation(semanticModel, invocation, declared, cancellationToken);
                }
                else if (node is AttributeSyntax attribute)
                {
                    CollectAttribute(semanticModel, attribute, declared, cancellationToken);
                }
            }
        }

        // No endpoints are named in this compilation at all — return an empty index so the analyzer stays
        // silent. Configured names alone don't activate it: they exist to supplement local declarations.
        if (declared.Count == 0)
        {
            return ImmutableHashSet<string>.Empty;
        }

        // Names declared in other projects can be listed in .editorconfig / a global analyzer config
        // (cairn_additional_route_names = a, b) or via an MSBuild CompilerVisibleProperty
        // (CairnAdditionalRouteNames), so multi-project solutions can silence cross-project references.
        foreach (var configured in AdditionalRouteNames(compilation, options))
        {
            declared.Add(configured);
        }

        return declared.ToImmutable();
    }

    private static void CollectInvocation(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        ImmutableHashSet<string>.Builder declared,
        CancellationToken cancellationToken)
    {
        var name = SimpleInvocationName(invocation);
        if (name == "WithName")
        {
            // Only an endpoint-builder WithName extension declares a route name. Cairn.LinkTarget.WithName
            // sets a per-link HAL name — collecting it would let LinkTarget.Route("x").WithName("y") pass
            // "y" off as a declared route — and any other instance method named WithName is a builder of
            // something else entirely.
            var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            var symbol = (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
            if (symbol is { IsExtensionMethod: true }
                && !IsCairnLinkTarget(symbol.ContainingType)
                && StringArgument(semanticModel, invocation, symbol, cancellationToken) is { } endpoint)
            {
                declared.Add(endpoint.Value);
            }
        }
        else if (name is "MapControllerRoute" or "MapAreaControllerRoute")
        {
            // Conventional routing declares a route name as the first (string) argument.
            var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            var symbol = (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
            var route = symbol is not null
                ? StringArgument(semanticModel, invocation, symbol, cancellationToken)
                : StringArgument(semanticModel, invocation, parameterName: "name", ordinal: 0, cancellationToken);
            if (route is { } conventional)
            {
                declared.Add(conventional.Value);
            }
        }
    }

    // Collect named controller routes: [HttpGet(Name = "...")], [Route("...", Name = "...")], etc.
    // Name = arguments resolve through GetConstantValue — exactly as StringArgument does for WithName,
    // and mirroring the generator's TypedConstant resolution — so a name declared via nameof(...) or a const
    // is collected instead of false-positiving every reference to it.
    private static void CollectAttribute(
        SemanticModel semanticModel,
        AttributeSyntax attribute,
        ImmutableHashSet<string>.Builder declared,
        CancellationToken cancellationToken)
    {
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
                declared.Add(literal.Token.ValueText);
            }
            else if (semanticModel.GetConstantValue(argument.Expression, cancellationToken) is { HasValue: true, Value: string name })
            {
                declared.Add(name);
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
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - "Attribute".Length);
        }

        return name is "Route" or "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch" or "HttpHead" or "HttpOptions";
    }

    private static IEnumerable<string> AdditionalRouteNames(Compilation compilation, AnalyzerOptions options)
    {
        var provider = options.AnalyzerConfigOptionsProvider;
        var raw = new List<string>();

        if (provider.GlobalOptions.TryGetValue("cairn_additional_route_names", out var global))
        {
            raw.Add(global);
        }

        if (provider.GlobalOptions.TryGetValue("build_property.CairnAdditionalRouteNames", out var property))
        {
            raw.Add(property);
        }

        foreach (var tree in compilation.SyntaxTrees)
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

    // The invocation must bind to Route/RouteTemplate on Cairn.LinkTarget (checked by metadata name via the
    // semantic model): a look-alike LinkTarget in another namespace is not a Cairn link reference, while
    // `using static Cairn.LinkTarget` and type aliases — invisible to a syntax check — are.
    private static IMethodSymbol? LinkTargetMethod(SemanticModel semanticModel, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
    {
        var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);
        var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);

        return symbol is IMethodSymbol { Name: "Route" or "RouteTemplate" } method && IsCairnLinkTarget(method.ContainingType)
            ? method
            : null;
    }

    private static bool IsCairnLinkTarget(INamedTypeSymbol? type)
        => type is { Name: "LinkTarget", ContainingType: null }
            && type.ContainingNamespace is { Name: "Cairn", ContainingNamespace.IsGlobalNamespace: true };

    // The argument bound to the method's first string parameter — the route name on Route/RouteTemplate,
    // WithName, and MapControllerRoute/MapAreaControllerRoute alike. Arguments map by NameColon when named
    // and by position otherwise, so a call with reordered named arguments
    // (Route(routeValues: v, routeName: "x")) validates instead of silently escaping analysis.
    private static (string Value, Location Location)? StringArgument(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.SpecialType == SpecialType.System_String)
            {
                return StringArgument(semanticModel, invocation, parameter.Name, parameter.Ordinal, cancellationToken);
            }
        }

        return null;
    }

    // A string literal, or any expression the compiler can evaluate to a constant string (a const field,
    // nameof(...), concatenated constants) — so names factored into constants neither false-positive as
    // undeclared nor escape validation as references.
    private static (string Value, Location Location)? StringArgument(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        string parameterName,
        int ordinal,
        CancellationToken cancellationToken)
    {
        ArgumentSyntax? match = null;
        var arguments = invocation.ArgumentList.Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.NameColon is { } named)
            {
                if (named.Name.Identifier.ValueText == parameterName)
                {
                    match = argument;
                    break;
                }
            }
            else if (i == ordinal)
            {
                match = argument;
                break;
            }
        }

        if (match is null)
        {
            return null;
        }

        if (match.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return (literal.Token.ValueText, literal.GetLocation());
        }

        var constant = semanticModel.GetConstantValue(match.Expression, cancellationToken);
        return constant is { HasValue: true, Value: string name }
            ? (name, match.Expression.GetLocation())
            : null;
    }

    private static string? ClosestMatch(string value, IEnumerable<string> candidates)
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
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[source.Length, target.Length];
    }
}
