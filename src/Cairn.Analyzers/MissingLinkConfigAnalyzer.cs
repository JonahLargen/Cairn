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
/// Flags a <c>.WithLinks()</c> endpoint whose handler returns a type with no <c>LinkConfig&lt;T&gt;</c>
/// declared anywhere in the compilation — the classic silent no-op where the endpoint opted into hypermedia
/// but every response serializes without it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingLinkConfigAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "CAIRN002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "WithLinks endpoint returns a type with no LinkConfig",
        messageFormat: "This WithLinks() endpoint returns '{0}', but no LinkConfig<{0}> (or a base type's) is declared in this compilation, so it will serialize without hypermedia",
        category: "Cairn",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An endpoint that opts into hypermedia with WithLinks() only emits links for types with a registered LinkConfig<T>. Declare one for the returned type (and register it via AddLinks / AddLinksFromAssembly), or remove WithLinks().",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly ImmutableHashSet<string> MapMethods = ImmutableHashSet.Create(
        "Map", "MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete", "MapMethods");

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
        var configured = new ConcurrentDictionary<ITypeSymbol, byte>(SymbolEqualityComparer.Default);
        var returned = new ConcurrentBag<(ITypeSymbol Type, Location Location)>();

        context.RegisterSymbolAction(
            symbolContext => CollectConfig((INamedTypeSymbol)symbolContext.Symbol, configured),
            SymbolKind.NamedType);

        context.RegisterSyntaxNodeAction(
            nodeContext => CollectEndpoint(nodeContext, returned),
            SyntaxKind.InvocationExpression);

        context.RegisterCompilationEndAction(endContext => Report(endContext, configured, returned));
    }

    // A type deriving LinkConfig<T> (directly or transitively) configures T.
    private static void CollectConfig(INamedTypeSymbol symbol, ConcurrentDictionary<ITypeSymbol, byte> configured)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current is { Name: "LinkConfig", IsGenericType: true, TypeArguments.Length: 1 })
            {
                configured.TryAdd(current.TypeArguments[0], 0);
                return;
            }
        }
    }

    private static void CollectEndpoint(SyntaxNodeAnalysisContext context, ConcurrentBag<(ITypeSymbol, Location)> returned)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WithLinks" } member)
        {
            return;
        }

        // Walk the builder chain (MapGet(...).WithName(...).WithLinks()) down to the Map* call. A WithLinks
        // on a route group (MapGroup) has no handler here — its endpoints are out of reach, so skip it.
        if (FindMapInvocation(member.Expression) is not { } map || HandlerReturnType(context, map) is not { } returnType)
        {
            return;
        }

        foreach (var candidate in UnwrapReturnType(returnType))
        {
            returned.Add((candidate, member.Name.GetLocation()));
        }
    }

    private static InvocationExpressionSyntax? FindMapInvocation(ExpressionSyntax expression)
    {
        while (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } invocation)
        {
            var name = inner.Name.Identifier.ValueText;
            if (MapMethods.Contains(name))
            {
                return invocation;
            }

            if (name == "MapGroup")
            {
                return null;
            }

            expression = inner.Expression;
        }

        return null;
    }

    // The endpoint handler is the delegate argument of the Map* call; its return type is what serializes.
    private static ITypeSymbol? HandlerReturnType(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax map)
    {
        foreach (var argument in map.ArgumentList.Arguments)
        {
            switch (argument.Expression)
            {
                case AnonymousFunctionExpressionSyntax lambda
                    when context.SemanticModel.GetSymbolInfo(lambda, context.CancellationToken).Symbol is IMethodSymbol method:
                    return method.ReturnType;

                case IdentifierNameSyntax or MemberAccessExpressionSyntax
                    when MethodGroup(context, argument.Expression) is { } group:
                    return group.ReturnType;
            }
        }

        return null;
    }

    private static IMethodSymbol? MethodGroup(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        var info = context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken);
        return info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    // Peel the wrappers minimal APIs put around the payload — Task/ValueTask, result unions and value-results
    // (Microsoft.AspNetCore.Http.HttpResults), arrays, sequences, and Cairn's paging envelopes — down to the
    // DTO types the serializer will see.
    private static IEnumerable<ITypeSymbol> UnwrapReturnType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            foreach (var inner in UnwrapReturnType(array.ElementType))
            {
                yield return inner;
            }

            yield break;
        }

        if (type is not INamedTypeSymbol named || named.SpecialType is SpecialType.System_String or SpecialType.System_Object)
        {
            yield break;
        }

        if (named.IsGenericType)
        {
            var definition = named.ConstructedFrom;
            var container = definition.ContainingNamespace?.ToDisplayString() ?? string.Empty;

            var unwraps =
                definition.Name is "Task" or "ValueTask" && container == "System.Threading.Tasks"
                || container == "Microsoft.AspNetCore.Http.HttpResults"
                || definition.Name is "PagedResource" or "CursorPage" && container == "Cairn.AspNetCore"
                || IsSequence(named);

            if (unwraps)
            {
                foreach (var argument in named.TypeArguments)
                {
                    foreach (var inner in UnwrapReturnType(argument))
                    {
                        yield return inner;
                    }
                }

                yield break;
            }
        }

        if (IsCandidate(named))
        {
            yield return named;
        }
    }

    private static bool IsSequence(INamedTypeSymbol named)
        => named is { Name: "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "IList" or "ICollection" or "List" }
            && named.ContainingNamespace?.ToDisplayString() is "System.Collections.Generic" or "System.Linq";

    // Only user-defined reference types can carry hypermedia (links correlate by reference); framework and
    // system types are never LinkConfig subjects.
    private static bool IsCandidate(INamedTypeSymbol named)
    {
        if (named.TypeKind != TypeKind.Class || named.IsAnonymousType || named.SpecialType != SpecialType.None)
        {
            return false;
        }

        var space = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return !space.StartsWith("System", System.StringComparison.Ordinal)
            && !space.StartsWith("Microsoft", System.StringComparison.Ordinal);
    }

    private static void Report(
        CompilationAnalysisContext context,
        ConcurrentDictionary<ITypeSymbol, byte> configured,
        ConcurrentBag<(ITypeSymbol Type, Location Location)> returned)
    {
        // No LinkConfig<T> lives in this compilation at all — registrations are likely in another project,
        // where this analyzer cannot see them. Stay silent rather than flag every endpoint.
        if (configured.IsEmpty)
        {
            return;
        }

        var reported = new HashSet<(ITypeSymbol, Location)>();
        foreach (var (type, location) in returned)
        {
            if (IsConfigured(type, configured) || !reported.Add((type, location)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name));
        }
    }

    // Configs apply to derived resources too (runtime dispatch walks base types), so check the whole chain.
    private static bool IsConfigured(ITypeSymbol type, ConcurrentDictionary<ITypeSymbol, byte> configured)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (configured.ContainsKey(current))
            {
                return true;
            }
        }

        return false;
    }
}
