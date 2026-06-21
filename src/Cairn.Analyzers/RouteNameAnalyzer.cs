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
        messageFormat: "No endpoint is named '{0}' via WithName{1}, so the link will not resolve at runtime",
        category: "Cairn",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A LinkTarget.Route name should match an endpoint registered with WithName.",
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
            nodeContext => Collect((InvocationExpressionSyntax)nodeContext.Node, routeNames, references),
            SyntaxKind.InvocationExpression);

        context.RegisterCompilationEndAction(endContext => Report(endContext, routeNames, references));
    }

    private static void Collect(
        InvocationExpressionSyntax invocation,
        ConcurrentDictionary<string, byte> routeNames,
        ConcurrentBag<(string, Location)> references)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return;
        }

        var method = member.Name.Identifier.ValueText;

        if (method == "WithName")
        {
            if (FirstStringLiteral(invocation) is { } endpoint)
            {
                routeNames.TryAdd(endpoint.Value, 0);
            }
        }
        else if (method == "Route" && IsLinkTargetReceiver(member.Expression))
        {
            if (FirstStringLiteral(invocation) is { } route)
            {
                references.Add((route.Value, route.Location));
            }
        }
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

    private static bool IsLinkTargetReceiver(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "LinkTarget",
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "LinkTarget",
        _ => false,
    };

    private static (string Value, Location Location)? FirstStringLiteral(InvocationExpressionSyntax invocation)
    {
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault();
        return argument?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? (literal.Token.ValueText, literal.GetLocation())
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
