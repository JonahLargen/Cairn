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

        Assert.Contains("public static partial class Routes", generated);
        Assert.Contains("public static global::Cairn.LinkTarget GetOrderById(int id)", generated);
        Assert.Contains(@"Route(""GetOrderById"", new { id })", generated);
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
    public void Maps_optional_constrained_parameter_to_its_type()
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

        Assert.Contains("public static global::Cairn.LinkTarget GetOptionalOrder(int id)", generated);
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
        Assert.Contains(@"Route(""GetUserOrder"", new { userId, id })", generated);
    }

    [Fact]
    public void Generates_route_methods_from_controller_attributes()
    {
        const string source = @"
class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
class HttpGetAttribute : System.Attribute { public HttpGetAttribute() {} public HttpGetAttribute(string template) {} public string Name { get; set; } }

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
        Assert.Contains(@"Route(""GetCustomerById"", new { id })", generated);
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
class RouteAttribute : System.Attribute { public RouteAttribute(string template) {} public string Name { get; set; } }
class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) {} public string Name { get; set; } }

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

        Assert.Contains(diagnostics, d => d.Id == "CAIRN002");
    }

    private static string Run(string source) => RunDriver(source).GeneratedTrees.Single().ToString();

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
