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
    public async Task Does_not_flag_a_name_declared_by_a_controller_route_attribute_via_nameof()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
class OrdersController
{
    [HttpGet(""orders/{id}"", Name = nameof(GetOrder))]
    public object GetOrder() => null;
    object Link() => Cairn.LinkTarget.Route(""GetOrder"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // The generator resolves nameof/const attribute names into the catalog; the analyzer must match,
        // or the reference above false-positives as undeclared.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ignores_a_look_alike_LinkTarget_from_another_namespace()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
namespace Other { public static class LinkTarget { public static object Route(string name) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Link() => Other.LinkTarget.Route(""NotARouteName"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // Only Cairn.LinkTarget.Route references are link references; a same-named type elsewhere is not.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Flags_a_reference_made_through_using_static()
    {
        const string source = @"
using static Cairn.LinkTarget;
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Link() => Route(""NotARouteName"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // A using-static call site binds to Cairn.LinkTarget.Route even without the receiver syntax.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("NotARouteName", diagnostic.GetMessage());
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

    [Fact]
    public async Task Flags_an_unknown_route_template_name()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string routeName, object routeValues = null) => routeName; public static object RouteTemplate(string routeName, object routeValues = null) => routeName; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""SearchOrders""); }
    object Link() => Cairn.LinkTarget.RouteTemplate(""SearchOrder"");
}";

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("SearchOrder", diagnostic.GetMessage());
        Assert.Equal("SearchOrders", diagnostic.Properties["suggestion"]);
    }

    [Fact]
    public async Task Validates_reordered_named_arguments()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string routeName, object routeValues = null) => routeName; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Link() => Cairn.LinkTarget.Route(routeValues: new { id = 1 }, routeName: ""GetOrderr"");
}";

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("GetOrderr", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_link_target_with_name_is_not_a_route_declaration()
    {
        // Mirrors the real shape: LinkTarget.WithName is an instance method (the per-link HAL name),
        // which always binds ahead of any WithName extension.
        const string source = @"
namespace Cairn { public class LinkTarget { public static LinkTarget Route(string routeName, object routeValues = null) => new LinkTarget(); public LinkTarget WithName(string name) => this; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Link() => Cairn.LinkTarget.Route(""GetOrder"").WithName(""payment"");
    object Broken() => Cairn.LinkTarget.Route(""payment"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // The per-link HAL name set by LinkTarget.WithName must not register 'payment' as a route name.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("payment", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Collects_conventional_controller_route_names()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string routeName, object routeValues = null) => routeName; } }
public static class EndpointExtensions
{
    public static object MapControllerRoute(this object app, string name, string pattern) => app;
    public static object MapAreaControllerRoute(this object app, string name, string areaName, string pattern) => app;
}
class Config
{
    void Endpoints(object app)
    {
        app.MapControllerRoute(""default"", ""{controller}/{action}"");
        app.MapAreaControllerRoute(""admin"", ""Admin"", ""admin/{controller}/{action}"");
    }
    object Default() => Cairn.LinkTarget.Route(""default"");
    object Admin() => Cairn.LinkTarget.Route(""admin"");
    object Broken() => Cairn.LinkTarget.Route(""defualt"");
}";

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("defualt", diagnostic.GetMessage());
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
