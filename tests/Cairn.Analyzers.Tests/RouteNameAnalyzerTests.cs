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

    [Fact]
    public async Task Does_not_flag_a_name_declared_by_a_controller_route_attribute()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
class OrdersController
{
    [HttpGet(""orders/{id}"", Name = ""GetOrder"")]
    public object Get() => null;
    object Link() => Cairn.LinkTarget.Route(""GetOrder"");
}";

        var diagnostics = await AnalyzeAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Does_not_flag_dynamic_route_names()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Interpolated(int id) => Cairn.LinkTarget.Route($""GetOrder{id}"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // A name only known at runtime is out of scope.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_name_declared_through_a_constant_is_collected()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
static class Names { public const string GetOrder = ""GetOrder""; }
class Config
{
    void Endpoints() { new object().WithName(Names.GetOrder); }
    object Literal() => Cairn.LinkTarget.Route(""GetOrder"");
    object Constant() => Cairn.LinkTarget.Route(Names.GetOrder);
}";

        var diagnostics = await AnalyzeAsync(source);

        // Without constant resolution the WithName(const) declaration was invisible and both references false-positived.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_mistyped_constant_reference_is_flagged()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
static class Names { public const string Wrong = ""GetOrderr""; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Constant() => Cairn.LinkTarget.Route(Names.Wrong);
}";

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("GetOrderr", diagnostic.GetMessage());
        Assert.Contains("GetOrder", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Names_from_analyzer_config_suppress_cross_project_references()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""LocalRoute""); }
    object Link() => Cairn.LinkTarget.Route(""DeclaredElsewhere"");
}";

        var flagged = await AnalyzeAsync(source);
        Assert.Single(flagged);

        var suppressed = await AnalyzeAsync(source, new Dictionary<string, string>
        {
            ["cairn_additional_route_names"] = "DeclaredElsewhere, AnotherOne",
        });
        Assert.Empty(suppressed);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, Dictionary<string, string>? globalConfig = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var options = new AnalyzerOptions([], new TestConfigProvider(globalConfig ?? []));
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RouteNameAnalyzer()), options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == RouteNameAnalyzer.DiagnosticId)];
    }

    private sealed class TestConfigProvider(Dictionary<string, string> global) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestConfigOptions(global);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestConfigOptions([]);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new TestConfigOptions([]);
    }

    private sealed class TestConfigOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
