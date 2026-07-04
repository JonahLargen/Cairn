using System.Collections.Immutable;
using Cairn.Analyzers;
using Cairn.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers.Tests;

public class RouteNameCodeFixBranchTests
{
    [Fact]
    public void Exposes_the_fixable_id_and_a_batch_fix_all_provider()
    {
        var provider = new RouteNameCodeFixProvider();

        // The host discovers both through these members; neither is exercised by applying a single fix.
        var id = Assert.Single(provider.FixableDiagnosticIds);
        Assert.Equal(RouteNameAnalyzer.DiagnosticId, id);
        Assert.Same(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    [Fact]
    public async Task No_fix_is_offered_for_a_constant_route_name_reference()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
static class Names { public const string Wrong = ""GetOrderByIdd""; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrderById""); }
    object Link() { return Cairn.LinkTarget.Route(Names.Wrong); }
}";

        var (document, diagnostic) = await GetDiagnosticAsync(source);
        var actions = await RegisterAsync(document, diagnostic);

        // The diagnostic points at the constant reference — there is no string literal to rewrite, so the
        // fix must stay away rather than mangle the constant.
        Assert.Empty(actions);
    }

    [Fact]
    public async Task A_fix_is_offered_when_the_diagnostic_spans_the_whole_invocation()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrderById""); }
    object Link() { return Cairn.LinkTarget.Route(""GetOrderByIdd""); }
}";

        var (document, diagnostic) = await GetDiagnosticAsync(source);

        // A diagnostic located on the enclosing invocation (as a third-party analyzer or an older Cairn
        // version might report it) still finds the literal through the descendant search.
        var root = await document.GetSyntaxRootAsync();
        var invocation = root!.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(node => node.ToString().Contains("GetOrderByIdd"));
        var relocated = Diagnostic.Create(
            Descriptor(),
            invocation.GetLocation(),
            ImmutableDictionary<string, string?>.Empty.Add("suggestion", "GetOrderById"));

        var actions = await RegisterAsync(document, relocated);

        var action = Assert.Single(actions);
        Assert.Contains("GetOrderById", action.Title);
    }

    [Fact]
    public async Task No_fix_is_offered_when_the_diagnostic_points_at_a_non_string_literal()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrderById""); }
    object Link() { return Cairn.LinkTarget.Route(""GetOrderByIdd"", 42); }
}";

        var (document, diagnostic) = await GetDiagnosticAsync(source);

        // A diagnostic located on the numeric literal finds a literal node, but not a string one.
        var root = await document.GetSyntaxRootAsync();
        var numeric = root!.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Single(node => node.IsKind(SyntaxKind.NumericLiteralExpression));
        var relocated = Diagnostic.Create(
            Descriptor(),
            numeric.GetLocation(),
            ImmutableDictionary<string, string?>.Empty.Add("suggestion", "GetOrderById"));

        var actions = await RegisterAsync(document, relocated);

        Assert.Empty(actions);
    }

    private static DiagnosticDescriptor Descriptor() => new(
        RouteNameAnalyzer.DiagnosticId, "title", "message", "Cairn", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static async Task<List<CodeAction>> RegisterAsync(Document document, Diagnostic diagnostic)
    {
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new RouteNameCodeFixProvider().RegisterCodeFixesAsync(context);
        return actions;
    }

    private static async Task<(Document Document, Diagnostic Diagnostic)> GetDiagnosticAsync(string source)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace
            .AddProject("Test", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = project.AddDocument("Test.cs", source);

        var compilation = await document.Project.GetCompilationAsync();
        var withAnalyzers = compilation!.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RouteNameAnalyzer()));
        var diagnostic = (await withAnalyzers.GetAnalyzerDiagnosticsAsync())
            .Single(d => d.Id == RouteNameAnalyzer.DiagnosticId);

        return (document, diagnostic);
    }
}
