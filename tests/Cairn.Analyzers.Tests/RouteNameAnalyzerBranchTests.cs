using System.Collections.Immutable;
using Cairn.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cairn.Analyzers.Tests;

public class RouteNameAnalyzerBranchTests
{
    [Fact]
    public async Task An_invocation_that_is_neither_member_access_nor_identifier_is_ignored()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    static void Generic<T>() { }
    void Endpoints() { new object().WithName(""GetOrder""); Generic<int>(); }
    object Link() => Cairn.LinkTarget.Route(""GetOrder"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // Generic<int>() has a GenericNameSyntax expression — neither member access nor plain identifier —
        // and must fall through the name pre-filter without crashing.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task An_ambiguous_with_name_does_not_declare_a_route()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class ExtensionsA { public static T WithName<T>(this T builder, string name) => builder; }
public static class ExtensionsB { public static T WithName<T>(this T builder, string name) => builder; }
class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
class Config
{
    [HttpGet(""orders"", Name = ""Real"")]
    public object Get() => null;
    void Endpoints() { new object().WithName(""Ghost""); }
    object Broken() => Cairn.LinkTarget.Route(""Ghost"");
    object Fine() => Cairn.LinkTarget.Route(""Real"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // Two applicable WithName extensions make the call ambiguous (no symbol, two candidates), so
        // 'Ghost' is never collected as a declaration.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("Ghost", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_with_name_that_fails_overload_resolution_with_one_candidate_still_declares()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints()
    {
        new object().WithName(""Anchor"");
        new object().WithName(""CandidateRoute"", 42);
        new object().WithName(5);
    }
    object Fine() => Cairn.LinkTarget.Route(""CandidateRoute"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // An extra argument breaks binding but leaves the single WithName candidate, whose string parameter
        // still maps 'CandidateRoute'; WithName(5) has a candidate too but no constant string to collect.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_with_name_extension_declared_inside_link_target_is_not_a_route_declaration()
    {
        const string source = @"
using Cairn;
namespace Cairn
{
    public static class LinkTarget
    {
        public static object Route(string name, object values = null) => name;
        public static T WithName<T>(this T builder, string name) => builder;
    }
}
class Config
{
    void Endpoints() { new object().WithName(""Anchor""); new object().WithName(""hal-name""); }
    object Broken() => Cairn.LinkTarget.Route(""hal-name-x"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // A WithName extension living on Cairn.LinkTarget itself is excluded... except that here every
        // WithName binds to it, so nothing is declared, the index is empty, and the analyzer stays silent.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task An_unbound_map_controller_route_still_declares_by_argument_position()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
class Config
{
    void Endpoints(object app)
    {
        // No MapControllerRoute extension exists in this compilation — the call has no symbol and no
        // candidates, so the analyzer falls back to the well-known 'name' parameter at ordinal 0.
        app.MapControllerRoute(""fallback-route"", ""{controller}/{action}"");
    }
    object Fine() => Cairn.LinkTarget.Route(""fallback-route"");
    object Broken() => Cairn.LinkTarget.Route(""fallback-rout"");
}";

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("fallback-rout", diagnostic.GetMessage());
        Assert.Equal("fallback-route", diagnostic.Properties["suggestion"]);
    }

    [Fact]
    public async Task A_map_controller_route_that_fails_overload_resolution_declares_via_its_candidate()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static object MapControllerRoute(this object app, string name, string pattern) => app; }
class Config
{
    void Endpoints(object app, string dynamicName)
    {
        app.MapControllerRoute(""candidate-route"", ""{controller}"", 42);
        // A non-constant name declares nothing — but must not crash the collection either.
        app.MapControllerRoute(dynamicName, ""{controller}"");
    }
    object Fine() => Cairn.LinkTarget.Route(""candidate-route"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // The extra argument breaks binding, but the lone candidate's 'name' parameter still resolves.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Bare_and_non_route_attributes_are_skipped()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
class HttpGetAttribute : System.Attribute { public HttpGetAttribute() {} public HttpGetAttribute(string template) {} public string Name { get; set; } }
class Marker : System.Attribute { }
class Config
{
    [HttpGet]
    [System.Obsolete(""old"")]
    [global::Marker]
    public object Bare() => null;

    [HttpGet(""orders"", Name = ""GetOrder"")]
    public object Get() => null;

    object Fine() => Cairn.LinkTarget.Route(""GetOrder"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // A route attribute with no argument list, a qualified non-route attribute, and an alias-qualified
        // attribute name each take a distinct skip path through the collector.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Recognizes_every_route_attribute_kind_and_the_explicit_attribute_suffix()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
namespace Mvc
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
    class HttpPostAttribute : System.Attribute { public HttpPostAttribute(string template) {} public string Name { get; set; } }
    class HttpPutAttribute : System.Attribute { public HttpPutAttribute(string template) {} public string Name { get; set; } }
    class HttpDeleteAttribute : System.Attribute { public HttpDeleteAttribute(string template) {} public string Name { get; set; } }
    class HttpPatchAttribute : System.Attribute { public HttpPatchAttribute(string template) {} public string Name { get; set; } }
    class HttpHeadAttribute : System.Attribute { public HttpHeadAttribute(string template) {} public string Name { get; set; } }
    class HttpOptionsAttribute : System.Attribute { public HttpOptionsAttribute(string template) {} public string Name { get; set; } }
}
class OrdersController
{
    [Mvc.Route(""r"", Name = ""ViaRoute"")]
    [Mvc.HttpPost(""p"", Name = ""ViaPost"")]
    [Mvc.HttpPut(""u"", Name = ""ViaPut"")]
    [Mvc.HttpDelete(""d"", Name = ""ViaDelete"")]
    [Mvc.HttpPatch(""m"", Name = ""ViaPatch"")]
    [Mvc.HttpHead(""h"", Name = ""ViaHead"")]
    [Mvc.HttpOptions(""o"", Name = ""ViaOptions"")]
    public object All() => null;

    [Mvc.HttpGetAttribute(""g"", Name = ""ViaSuffix"")]
    public object Suffixed() => null;

    object Links() => Cairn.LinkTarget.Route(""ViaRoute"") ?? Cairn.LinkTarget.Route(""ViaPost"")
        ?? Cairn.LinkTarget.Route(""ViaPut"") ?? Cairn.LinkTarget.Route(""ViaDelete"")
        ?? Cairn.LinkTarget.Route(""ViaPatch"") ?? Cairn.LinkTarget.Route(""ViaHead"")
        ?? Cairn.LinkTarget.Route(""ViaOptions"") ?? Cairn.LinkTarget.Route(""ViaSuffix"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // Every attribute kind — qualified, and with the explicit 'Attribute' suffix — declares its name.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Attribute_name_arguments_that_are_not_constant_strings_declare_nothing()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
class Config
{
    [HttpGet(""a"", Name = null)]
    public object NullName() => null;

    [HttpGet(""b"", Name = Undefined)]
    public object Unresolvable() => null;

    [HttpGet(""c"", Name = ""GetOrder"")]
    public object Get() => null;

    object Fine() => Cairn.LinkTarget.Route(""GetOrder"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // A constant-but-not-string Name (null) and a name that never resolves both fall out of collection.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Names_from_the_msbuild_property_suppress_cross_project_references()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""LocalRoute""); }
    object Link() => Cairn.LinkTarget.Route(""DeclaredElsewhere"");
}";

        var suppressed = await AnalyzeAsync(source, new Dictionary<string, string>
        {
            ["build_property.CairnAdditionalRouteNames"] = "DeclaredElsewhere",
        });

        // The CompilerVisibleProperty spelling must work exactly like the .editorconfig key.
        Assert.Empty(suppressed);
    }

    [Fact]
    public async Task Names_from_per_tree_analyzer_config_suppress_cross_project_references()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""LocalRoute""); }
    object Link() => Cairn.LinkTarget.Route(""DeclaredElsewhere"");
}";

        var suppressed = await AnalyzeAsync(source, treeConfig: new Dictionary<string, string>
        {
            ["cairn_additional_route_names"] = "DeclaredElsewhere",
        });

        // A section-scoped .editorconfig value surfaces through per-tree options, not GlobalOptions.
        Assert.Empty(suppressed);
    }

    [Fact]
    public async Task A_route_call_that_fails_overload_resolution_is_still_validated()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Broken() => Cairn.LinkTarget.Route(""GetOrderr"", 1, 2);
}";

        var diagnostics = await AnalyzeAsync(source);

        // Too many arguments leaves no symbol but one candidate — the reference must not escape validation.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("GetOrderr", diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_route_call_with_no_binding_candidates_is_ignored()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Broken() => Cairn.LinkTarget.Route(""NotARouteName"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // With no Route method on LinkTarget at all there is nothing to bind to — no symbol, zero
        // candidates — so the call cannot be treated as a link reference.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_delegate_named_route_is_not_a_link_reference()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Call(System.Func<string, object> Route) => Route(""NotARouteName"");
}";

        var diagnostics = await AnalyzeAsync(source);

        // Invoking a delegate named Route binds to its Invoke method, which is not LinkTarget.Route.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_route_overload_without_a_string_parameter_is_ignored()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(int id) => id; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Link() => Cairn.LinkTarget.Route(5);
}";

        var diagnostics = await AnalyzeAsync(source);

        // A Route method with no string parameter carries no route name to validate.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_route_call_with_no_arguments_is_ignored()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Link() => Cairn.LinkTarget.Route();
}";

        var diagnostics = await AnalyzeAsync(source);

        // No argument ever maps to the name parameter, so there is no value (or location) to report.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_null_route_name_is_ignored()
    {
        const string source = @"
namespace Cairn { public static class LinkTarget { public static object Route(string name, object values = null) => name; } }
public static class EndpointExtensions { public static T WithName<T>(this T builder, string name) => builder; }
class Config
{
    void Endpoints() { new object().WithName(""GetOrder""); }
    object Link() => Cairn.LinkTarget.Route(null);
}";

        var diagnostics = await AnalyzeAsync(source);

        // The null literal is a constant, but not a string constant — nothing to validate.
        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        Dictionary<string, string>? globalConfig = null,
        Dictionary<string, string>? treeConfig = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var options = new AnalyzerOptions([], new TestConfigProvider(globalConfig ?? [], treeConfig ?? []));
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RouteNameAnalyzer()), options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == RouteNameAnalyzer.DiagnosticId)];
    }

    private sealed class TestConfigProvider(Dictionary<string, string> global, Dictionary<string, string> perTree) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestConfigOptions(global);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestConfigOptions(perTree);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new TestConfigOptions([]);
    }

    private sealed class TestConfigOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
