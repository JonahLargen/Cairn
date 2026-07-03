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
/// Flags an endpoint that opted into hypermedia — a <c>.WithLinks()</c> minimal-API endpoint or a
/// <c>[CairnLinks]</c> controller action — whose handler returns a type with no <c>LinkConfig&lt;T&gt;</c>
/// declared anywhere in the compilation: the classic silent no-op where every response serializes without links.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingLinkConfigAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id reported by this analyzer.</summary>
    public const string DiagnosticId = "CAIRN002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "WithLinks endpoint returns a type with no LinkConfig",
        messageFormat: "This hypermedia-enabled endpoint returns '{0}', but no LinkConfig<{0}> (or a base type's) is declared in this compilation, so it will serialize without hypermedia",
        category: "Cairn",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An endpoint that opts into hypermedia with WithLinks() or [CairnLinks] only emits links for types with a registered LinkConfig<T>. Declare one for the returned type (and register it via AddLinks / AddLinksFromAssembly), remove the opt-in, or — when the config lives in another project — list the type in cairn_additional_configured_types (.editorconfig) or the CairnAdditionalConfiguredTypes MSBuild property.",
        helpLinkUri: "https://jonahlargen.github.io/Cairn/articles/route-safety.html#cairn002-withlinks-endpoint-with-no-linkconfig",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly ImmutableHashSet<string> MapMethods = ImmutableHashSet.Create(
        "Map", "MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete", "MapMethods", "MapFallback");

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

        context.RegisterSymbolAction(
            symbolContext => CollectControllerAction((IMethodSymbol)symbolContext.Symbol, returned),
            SymbolKind.Method);

        context.RegisterSyntaxNodeAction(
            nodeContext => CollectEndpoint(nodeContext, returned),
            SyntaxKind.InvocationExpression);

        context.RegisterCompilationEndAction(endContext => Report(endContext, configured, returned));
    }

    // A type deriving Cairn's LinkConfig<T> (directly or transitively) configures T. The base must be the
    // real Cairn.LinkConfig<T>: a look-alike LinkConfig from another namespace configures nothing.
    private static void CollectConfig(INamedTypeSymbol symbol, ConcurrentDictionary<ITypeSymbol, byte> configured)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current is { Name: "LinkConfig", IsGenericType: true, TypeArguments.Length: 1 }
                && current.ContainingNamespace is { Name: "Cairn", ContainingNamespace.IsGlobalNamespace: true })
            {
                configured.TryAdd(current.TypeArguments[0], 0);
                return;
            }
        }
    }

    // The controller counterpart to .WithLinks(): a public action carrying [CairnLinks] — on the method, its
    // controller, or a base controller (the attribute is inherited) — opts its responses into hypermedia.
    private static void CollectControllerAction(IMethodSymbol method, ConcurrentBag<(ITypeSymbol, Location)> returned)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.DeclaredAccessibility != Accessibility.Public)
        {
            return;
        }

        if (!HasCairnLinks(method))
        {
            return;
        }

        var location = method.Locations.FirstOrDefault() ?? Location.None;
        foreach (var candidate in UnwrapReturnType(method.ReturnType))
        {
            returned.Add((candidate, location));
        }
    }

    private static bool HasCairnLinks(IMethodSymbol method)
    {
        if (HasCairnLinksAttribute(method.GetAttributes()))
        {
            return true;
        }

        for (var type = method.ContainingType; type is not null; type = type.BaseType)
        {
            if (HasCairnLinksAttribute(type.GetAttributes()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCairnLinksAttribute(ImmutableArray<AttributeData> attributes)
        => attributes.Any(static attribute => attribute.AttributeClass is { Name: "CairnLinksAttribute" } type
            && IsCairnNamespace(type.ContainingNamespace));

    private static void CollectEndpoint(SyntaxNodeAnalysisContext context, ConcurrentBag<(ITypeSymbol, Location)> returned)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WithLinks" } member)
        {
            return;
        }

        // The call must bind to Cairn's WithLinks extension: a same-named extension from another library is
        // not a hypermedia opt-in, and flagging it would be pure noise.
        var info = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        var symbol = (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
        if (symbol is not { Name: "WithLinks", IsExtensionMethod: true }
            || !IsCairnNamespace(symbol.ContainingType.ContainingNamespace))
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

    private static bool IsCairnNamespace(INamespaceSymbol? space)
    {
        // Cairn or a nested Cairn.* namespace (WithLinks lives in Cairn.AspNetCore).
        for (var current = space; current is { IsGlobalNamespace: false }; current = current.ContainingNamespace)
        {
            if (current is { Name: "Cairn", ContainingNamespace.IsGlobalNamespace: true })
            {
                return true;
            }
        }

        return false;
    }

    private static InvocationExpressionSyntax? FindMapInvocation(ExpressionSyntax expression)
    {
        // Depth-bounded to defend against pathological (or cyclic) variable chains.
        for (var depth = 0; depth < 8; depth++)
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

            // A chain broken through a variable (`var e = app.MapGet(...); e.WithLinks();`) bottoms out at
            // an identifier — continue from the variable's initializer, mirroring the Routes generator.
            if (expression is IdentifierNameSyntax identifier && FindLocalInitializer(identifier) is { } initializer)
            {
                expression = initializer;
                continue;
            }

            return null;
        }

        return null;
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

    // Peel the wrappers minimal APIs and MVC put around the payload — Task/ValueTask, result unions and
    // value-results (Microsoft.AspNetCore.Http.HttpResults), ActionResult<T>, arrays, sequences, and Cairn's
    // paging envelopes — down to the DTO types the serializer will see.
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
                || definition.Name == "ActionResult" && container == "Microsoft.AspNetCore.Mvc"
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

        // Types configured in other projects can be listed in .editorconfig / a global analyzer config
        // (cairn_additional_configured_types = OrderDto, Contracts.CustomerDto) or via an MSBuild
        // CompilerVisibleProperty (CairnAdditionalConfiguredTypes) — matched by simple or fully qualified name.
        var additional = AdditionalConfiguredTypes(context);

        var reported = new HashSet<(ITypeSymbol, Location)>(ReportComparer.Instance);
        foreach (var (type, location) in returned)
        {
            if (IsConfigured(type, configured)
                || additional.Contains(type.Name)
                || additional.Contains(type.ToDisplayString())
                || !reported.Add((type, location)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name));
        }
    }

    private static ImmutableHashSet<string> AdditionalConfiguredTypes(CompilationAnalysisContext context)
    {
        var provider = context.Options.AnalyzerConfigOptionsProvider;
        var names = ImmutableHashSet.CreateBuilder<string>(System.StringComparer.Ordinal);
        var raw = new List<string>();

        if (provider.GlobalOptions.TryGetValue("cairn_additional_configured_types", out var global))
        {
            raw.Add(global);
        }

        if (provider.GlobalOptions.TryGetValue("build_property.CairnAdditionalConfiguredTypes", out var property))
        {
            raw.Add(property);
        }

        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            if (provider.GetOptions(tree).TryGetValue("cairn_additional_configured_types", out var perTree))
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
                    names.Add(trimmed);
                }
            }
        }

        return names.ToImmutable();
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

    // Symbols compare by SymbolEqualityComparer — the default tuple comparison is reference equality, under
    // which the same type from two analysis passes would double-report.
    private sealed class ReportComparer : IEqualityComparer<(ITypeSymbol Type, Location Location)>
    {
        public static readonly ReportComparer Instance = new();

        public bool Equals((ITypeSymbol Type, Location Location) x, (ITypeSymbol Type, Location Location) y)
            => SymbolEqualityComparer.Default.Equals(x.Type, y.Type) && x.Location.Equals(y.Location);

        public int GetHashCode((ITypeSymbol Type, Location Location) value)
            => (SymbolEqualityComparer.Default.GetHashCode(value.Type) * 397) ^ value.Location.GetHashCode();
    }
}
