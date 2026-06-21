using System.Collections.Immutable;
using Cairn.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers.Tests;

public class RouteNameAnalyzerTests
{
    [Fact]
    public async Task Flags_unknown_route_name_with_suggestion()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrderById""); }
    object Link() { return Cairn.LinkTarget.Route(""GetOrderByIdd""); }
}";

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(RouteNameAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("GetOrderByIdd", diagnostic.GetMessage());
        Assert.Contains("GetOrderById", diagnostic.GetMessage());
        Assert.Equal("GetOrderById", diagnostic.Properties["suggestion"]);
    }

    [Fact]
    public async Task Does_not_flag_known_route_name()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrderById""); }
    object Link() { return Cairn.LinkTarget.Route(""GetOrderById""); }
}";

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Does_not_flag_when_no_endpoints_are_named()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
class Config
{
    object Link() { return Cairn.LinkTarget.Route(""DefinedInAnotherProject""); }
}";

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RouteNameAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == RouteNameAnalyzer.DiagnosticId)];
    }
}
