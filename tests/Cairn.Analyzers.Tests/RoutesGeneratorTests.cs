using System.Collections.Immutable;
using Cairn.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cairn.Analyzers.Tests;

public class RoutesGeneratorTests
{
    [Fact]
    public void Generates_typed_route_methods()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/orders/{id:int}"", handler).WithName(""GetOrderById"");
        app.MapPost(""/orders/{id:int}/cancel"", handler).WithName(""CancelOrder"");
        app.MapGet(""/orders"", handler).WithName(""ListOrders"");
    }
}";

        var generated = Run(source);

        // internal: the catalog is app-internal, so it can't collide (or be CS0433-ambiguous) across projects.
        Assert.Contains("internal static partial class Routes", generated);
        Assert.Contains("public static global::Cairn.LinkTarget GetOrderById(int id)", generated);
        Assert.Contains(@"Route(""GetOrderById"", new global::System.Collections.Generic.Dictionary<string, object?> { [""id""] = id })", generated);
        Assert.Contains("public static global::Cairn.LinkTarget CancelOrder(int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget ListOrders()", generated);
        Assert.Contains(@"Route(""ListOrders"", null)", generated);
    }

    [Fact]
    public void Maps_route_constraints_to_types_and_sanitizes_names()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/users/{key:guid}/posts/{slug}"", handler).WithName(""get-user-post"");
    }
}";

        var generated = Run(source);

        Assert.Contains("public static global::Cairn.LinkTarget GetUserPost(global::System.Guid key, string slug)", generated);
    }

    [Fact]
    public void Maps_optional_constrained_parameter_to_a_nullable_defaulted_parameter()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/orders/{id:int?}"", handler).WithName(""GetOptionalOrder"");
    }
}";

        var generated = Run(source);

        // An optional route parameter is optional for callers too: nullable, defaulted, and skipped in the
        // route values when omitted — so the link resolves without the segment.
        Assert.Contains("public static global::Cairn.LinkTarget GetOptionalOrder(int? id = null)", generated);
        Assert.Contains("if (id is not null)", generated);
        Assert.Contains(@"__cairnRouteValues[""id""] = id;", generated);
        Assert.Contains(@"Route(""GetOptionalOrder"", __cairnRouteValues)", generated);
        Assert.DoesNotContain(CSharpSyntaxTree.ParseText(generated).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Maps_defaulted_and_range_constrained_parameters()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/legacy/{id=5}"", handler).WithName(""GetLegacy"");
        app.MapGet(""/big/{size:min(10)}"", handler).WithName(""GetBig"");
    }
}";

        var generated = Run(source);

        // A defaulted parameter is optional for callers; min/max/range are evaluated by ASP.NET as long.
        Assert.Contains("public static global::Cairn.LinkTarget GetLegacy(string? id = null)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget GetBig(long size)", generated);
    }

    [Fact]
    public void Mixes_required_and_optional_parameters_in_one_route()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/users/{userId:int}/posts/{page:int?}"", handler).WithName(""GetUserPosts"");
    }
}";

        var generated = Run(source);

        Assert.Contains("public static global::Cairn.LinkTarget GetUserPosts(int userId, int? page = null)", generated);
        Assert.Contains(@"__cairnRouteValues[""userId""] = userId;", generated);
        Assert.Contains("if (page is not null)", generated);
        Assert.DoesNotContain(CSharpSyntaxTree.ParseText(generated).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Route_values_local_does_not_collide_with_a_parameter_named_values()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/lookup/{values}/{page:int?}"", handler).WithName(""Lookup"");
    }
}";

        var generated = Run(source);

        // A route parameter named 'values' used to collide with the generated dictionary local (CS0136),
        // making the whole catalog fail to compile.
        Assert.Contains("public static global::Cairn.LinkTarget Lookup(string values, int? page = null)", generated);
        Assert.Contains(@"__cairnRouteValues[""values""] = values;", generated);
        AssertCatalogCompiles(generated);
    }

    [Fact]
    public void Route_values_local_is_suffix_renamed_when_a_parameter_claims_even_the_unlikely_name()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/odd/{__cairnRouteValues}/{page:int?}"", handler).WithName(""OddLookup"");
    }
}";

        var generated = Run(source);

        Assert.Contains("var __cairnRouteValues_ = new global::System.Collections.Generic.Dictionary<string, object?>();", generated);
        Assert.Contains(@"__cairnRouteValues_[""__cairnRouteValues""] = __cairnRouteValues;", generated);
        AssertCatalogCompiles(generated);
    }

    [Fact]
    public void Includes_map_group_prefix_parameters()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGroup(""/users/{userId:int}"").MapGet(""/{id:int}"", handler).WithName(""GetUserOrder"");
    }
}";

        var generated = Run(source);

        Assert.Contains("public static global::Cairn.LinkTarget GetUserOrder(int userId, int id)", generated);
        Assert.Contains(@"Route(""GetUserOrder"", new global::System.Collections.Generic.Dictionary<string, object?> { [""userId""] = userId, [""id""] = id })", generated);
    }

    [Fact]
    public void Includes_map_group_prefix_bound_to_a_variable()
    {
        const string source = @"
class Program
{
    static void M()
    {
        var users = app.MapGroup(""/users/{userId:int}"");
        var orders = users.MapGroup(""/orders"");
        orders.MapGet(""/{id:int}"", handler).WithName(""GetUserOrderViaVariable"");
    }
}";

        var generated = Run(source);

        // Group prefixes bound to locals are the common MapGroup shape; their route parameters must survive.
        Assert.Contains("public static global::Cairn.LinkTarget GetUserOrderViaVariable(int userId, int id)", generated);
        Assert.Contains(@"Route(""GetUserOrderViaVariable"", new global::System.Collections.Generic.Dictionary<string, object?> { [""userId""] = userId, [""id""] = id })", generated);
    }

    [Fact]
    public void Escapes_route_names_that_contain_quotes_or_backslashes()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/odd"", handler).WithName(""Get\""Odd\\Name"");
    }
}";

        var generated = Run(source);

        // The name must round-trip as a valid C# string literal, not break the generated file.
        Assert.Contains(@"Route(""Get\""Odd\\Name"", null)", generated);
        Assert.DoesNotContain(CSharpSyntaxTree.ParseText(generated).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generates_route_methods_from_controller_attributes()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
namespace Microsoft.AspNetCore.Mvc
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute() {} public HttpGetAttribute(string template) {} public string Name { get; set; } }
}

[Route(""customers"")]
class CustomersController
{
    [HttpGet(""{id:int}"", Name = ""GetCustomerById"")]
    public object Get(int id) => null;

    [HttpGet(Name = ""ListCustomers"")]
    public object List() => null;
}";

        var generated = Run(source);

        Assert.Contains("public static global::Cairn.LinkTarget GetCustomerById(int id)", generated);
        Assert.Contains(@"Route(""GetCustomerById"", new global::System.Collections.Generic.Dictionary<string, object?> { [""id""] = id })", generated);
        Assert.Contains("public static global::Cairn.LinkTarget ListCustomers()", generated);
    }

    [Fact]
    public void Deduplicates_a_repeated_route_parameter_name()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGroup(""/{id:int}"").MapGet(""/sub/{id:int}"", handler).WithName(""DupParam"");
    }
}";

        var generated = Run(source);

        // A repeated {id} must not generate (int id, int id) / new { id, id }, which would not compile.
        Assert.Contains("public static global::Cairn.LinkTarget DupParam(int id)", generated);
        Assert.DoesNotContain("int id, int id", generated);
        Assert.DoesNotContain("new { id, id }", generated);
    }

    [Fact]
    public void Tilde_rooted_controller_action_template_overrides_the_controller_prefix()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
namespace Microsoft.AspNetCore.Mvc
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
}

[Route(""api/{version}"")]
class ThingsController
{
    [HttpGet(""~/things/{id:int}"", Name = ""GetThing"")]
    public object Get(int id) => null;
}";

        var generated = Run(source);

        // '~/' is application-root-relative; the controller prefix's {version} must not appear.
        Assert.Contains("public static global::Cairn.LinkTarget GetThing(int id)", generated);
        Assert.DoesNotContain("version", generated);
    }

    [Fact]
    public void Reports_diagnostic_when_two_route_names_reduce_to_the_same_method()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/a"", handler).WithName(""GetUser"");
        app.MapGet(""/b"", handler).WithName(""get-user"");
    }
}";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, d => d.Id == "CAIRN003");
        // CAIRN002 belongs to the MissingLinkConfig analyzer; the generator must not reuse it.
        Assert.DoesNotContain(diagnostics, d => d.Id == "CAIRN002");
    }

    [Fact]
    public void Resolves_route_names_declared_through_constants_and_nameof()
    {
        const string source = @"
static class RouteNames { public const string GetOrder = ""GetOrder""; }
class Program
{
    static void GetOrderItem() {}
    static void M()
    {
        app.MapGet(""/orders/{id:int}"", handler).WithName(RouteNames.GetOrder);
        app.MapGet(""/orders/{id:int}/items/{itemId:int}"", handler).WithName(nameof(GetOrderItem));
    }
}";

        var generated = Run(source);

        // Names factored into constants or nameof(...) previously vanished from the catalog.
        Assert.Contains("public static global::Cairn.LinkTarget GetOrder(int id)", generated);
        Assert.Contains(@"Route(""GetOrder"", new global::System.Collections.Generic.Dictionary<string, object?> { [""id""] = id })", generated);
        Assert.Contains("public static global::Cairn.LinkTarget GetOrderItem(int id, int itemId)", generated);
    }

    [Fact]
    public void Resolves_controller_route_names_declared_through_nameof()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
namespace Microsoft.AspNetCore.Mvc
{
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
}

class WidgetsController
{
    [HttpGet(""widgets/{id:int}"", Name = nameof(GetWidget))]
    public object GetWidget(int id) => null;
}";

        var generated = Run(source);

        Assert.Contains("public static global::Cairn.LinkTarget GetWidget(int id)", generated);
        Assert.Contains(@"Route(""GetWidget"", new global::System.Collections.Generic.Dictionary<string, object?> { [""id""] = id })", generated);
    }

    [Fact]
    public void Emits_an_empty_routes_class_when_no_endpoints_are_named()
    {
        const string source = @"
class Program
{
    static void M() { }
}";

        var generated = Run(source);

        // Without the empty class, user code referencing Cairn.Routes fails to compile until the
        // first endpoint is named.
        Assert.Contains("internal static partial class Routes", generated);
        Assert.DoesNotContain("public static global::Cairn.LinkTarget", generated);
        Assert.DoesNotContain(CSharpSyntaxTree.ParseText(generated).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Renames_a_route_that_maps_to_a_reserved_method_name_and_reports_a_diagnostic()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/routes"", handler).WithName(""Routes"");
        app.MapGet(""/equals/{id:int}"", handler).WithName(""equals"");
        app.MapGet(""/text"", handler).WithName(""ToString"");
    }
}";

        var result = RunDriver(source);
        var generated = result.GeneratedTrees.Single().ToString();

        // 'Routes' would collide with the class name (CS0542); 'Equals'/'ToString' would hide
        // System.Object members (CS0108). Each is emitted under a '_' prefix instead of breaking the build.
        Assert.Contains("public static global::Cairn.LinkTarget _Routes()", generated);
        Assert.Contains(@"Route(""Routes"", null)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget _Equals(int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget _ToString()", generated);
        Assert.Equal(3, result.Diagnostics.Count(d => d.Id == "CAIRN004"));
        Assert.All(result.Diagnostics.Where(d => d.Id == "CAIRN004"), d => Assert.NotEqual(Location.None, d.Location));
        AssertCatalogCompiles(generated);
    }

    [Fact]
    public void A_regex_constraint_with_braces_does_not_truncate_or_drop_parameters()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/codes/{code:regex(^\\d{4}$)}/{id:int}"", handler).WithName(""GetCode"");
        app.MapGet(""/tags/{tag:regex(^[a-z]{{2,10}}$)}/{page:int}"", handler).WithName(""GetTag"");
    }
}";

        var generated = Run(source);

        // The first '}' inside the constraint's own braces used to end the parameter, mangling its name
        // and swallowing every parameter that followed.
        Assert.Contains("public static global::Cairn.LinkTarget GetCode(string code, int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget GetTag(string tag, int page)", generated);
    }

    [Fact]
    public void Includes_map_and_map_fallback_endpoints()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.Map(""/ping/{id:int}"", handler).WithName(""Ping"");
        app.MapFallback(""/spa/{*path}"", handler).WithName(""Spa"");
        app.MapFallback(handler).WithName(""Fallback"");
    }
}";

        var generated = Run(source);

        Assert.Contains("public static global::Cairn.LinkTarget Ping(int id)", generated);
        Assert.Contains("public static global::Cairn.LinkTarget Spa(string path)", generated);
        // The no-pattern MapFallback overload uses ASP.NET's implicit ""{*path:nonfile}"" template.
        Assert.Contains("public static global::Cairn.LinkTarget Fallback(string path)", generated);
    }

    [Fact]
    public void Inherits_the_route_prefix_from_a_base_controller()
    {
        const string source = @"
using Microsoft.AspNetCore.Mvc;
namespace Microsoft.AspNetCore.Mvc
{
    class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
    class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }
}

[Route(""api/v{version:int}"")]
abstract class ApiControllerBase
{
}

class OrdersController : ApiControllerBase
{
    [HttpGet(""orders/{id:int}"", Name = ""GetVersionedOrder"")]
    public object Get(int id) => null;
}";

        var generated = Run(source);

        // MVC applies a base controller's [Route] prefix when the derived controller declares none.
        Assert.Contains("public static global::Cairn.LinkTarget GetVersionedOrder(int version, int id)", generated);
    }

    [Fact]
    public void Reports_the_collision_diagnostic_at_the_losing_route_name()
    {
        const string source = @"
class Program
{
    static void M()
    {
        app.MapGet(""/a"", handler).WithName(""GetUser"");
        app.MapGet(""/b"", handler).WithName(""get-user"");
    }
}";

        var diagnostics = RunDiagnostics(source);

        var collision = Assert.Single(diagnostics, d => d.Id == "CAIRN003");
        // A navigable location — the dropped name's argument — instead of Location.None.
        Assert.NotEqual(Location.None, collision.Location);
        Assert.Contains("get-user", collision.GetMessage());
    }

    private static string Run(string source) => RunDriver(source).GeneratedTrees.Single().ToString();

    // Compiles the generated catalog against a LinkTarget stub, so name-binding errors the parser can't see
    // (e.g. a CS0136 local/parameter collision) fail the test.
    private static void AssertCatalogCompiles(string generated)
    {
        const string stub = @"
namespace Cairn
{
    public sealed class LinkTarget
    {
        public static LinkTarget Route(string routeName, object? values) => new LinkTarget();
    }
}";
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = CSharpCompilation.Create(
            "GeneratedCatalog",
            [CSharpSyntaxTree.ParseText(generated), CSharpSyntaxTree.ParseText(stub)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Collections.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    private static ImmutableArray<Diagnostic> RunDiagnostics(string source) => RunDriver(source).Diagnostics;

    private static GeneratorDriverRunResult RunDriver(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Test",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RoutesGenerator().AsSourceGenerator());
        driver = driver.RunGenerators(compilation);

        return driver.GetRunResult();
    }
}
