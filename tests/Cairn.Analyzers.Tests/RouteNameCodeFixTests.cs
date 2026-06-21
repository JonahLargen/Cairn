using System.Collections.Immutable;
using Cairn.Analyzers;
using Cairn.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers.Tests;

public class RouteNameCodeFixTests
{
    [Fact]
    public async Task Replaces_unknown_route_name_with_suggestion()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrderById""); }
    object Link() { return Cairn.LinkTarget.Route(""GetOrderByIdd""); }
}";

        var fixedSource = await ApplyFixAsync(source);

        Assert.Contains(@"Route(""GetOrderById"")", fixedSource);
        Assert.DoesNotContain("GetOrderByIdd", fixedSource);
    }

    private static async Task<string> ApplyFixAsync(string source)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace
            .AddProject("Test", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = project.AddDocument("Test.cs", source);

        var compilation = await document.Project.GetCompilationAsync();
        var withAnalyzers = compilation!.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RouteNameAnalyzer()));
        var diagnostic = (await withAnalyzers.GetAnalyzerDiagnosticsAsync())
            .Single(d => d.Id == RouteNameAnalyzer.DiagnosticId);

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new RouteNameCodeFixProvider().RegisterCodeFixesAsync(context);

        var operations = await actions.Single().GetOperationsAsync(CancellationToken.None);
        var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var text = await changedSolution.GetDocument(document.Id)!.GetTextAsync();
        return text.ToString();
    }
}
