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

    private static string Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Test",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RoutesGenerator().AsSourceGenerator());
        driver = driver.RunGenerators(compilation);

        return driver.GetRunResult().GeneratedTrees.Single().ToString();
    }
}
