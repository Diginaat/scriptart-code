using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;

namespace ScriptArt.Tests;

public sealed class ExternalApiTests
{
    private static string MainBody(GraphBuilder graph) => VariableTests.MainBody(graph);

    private static ExternalMethod Framework(string documentationId) =>
        Assert.Single(ExternalApi.Framework, method => method.DocumentationId == documentationId);

    [Fact]
    public void FrameworkMethodsWithSupportedSignaturesAreOfferedAndOthersAreFilteredOut()
    {
        ExternalMethod max = Framework("M:System.Math.Max(System.Int32,System.Int32)");
        Assert.Equal(("Math.Max(int, int)", true, TypeRef.Int32), (max.DisplayName, max.IsPure, max.ReturnType));
        Assert.Equal("dotnet:M:System.Math.Max(System.Int32,System.Int32)", max.TypeId);
        Assert.False(Framework("M:System.IO.File.WriteAllText(System.String,System.String)").IsPure);
        Assert.True(Framework("M:System.String.ToUpper").IsPure);

        // Unsupported types (long, decimal, arrays, char) and out parameters are not offered.
        Assert.DoesNotContain(ExternalApi.Framework, method => method.DocumentationId == "M:System.Math.Max(System.Int64,System.Int64)");
        Assert.DoesNotContain(ExternalApi.Framework, method => method.DocumentationId.StartsWith("M:System.IO.File.ReadAllLines", StringComparison.Ordinal));
        Assert.DoesNotContain(ExternalApi.Framework, method => method.DocumentationId.Contains("System.Char", StringComparison.Ordinal));
        Assert.DoesNotContain(ExternalApi.Framework, method => method.DocumentationId.Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public void PureMethodsAreInlinedAndOtherMethodsRunAsStatements()
    {
        var graph = new GraphBuilder();
        string entry = graph.Node(BuiltInNodes.EntryTypeId);
        string variable = graph.Node(Framework("M:System.Environment.GetEnvironmentVariable(System.String)").TypeId);
        string print = graph.Node("console.write-line");
        string upper = graph.Node(Framework("M:System.String.ToUpper").TypeId);
        string max = graph.Node(Framework("M:System.Math.Max(System.Int32,System.Int32)").TypeId);
        string printMax = graph.Node("console.write-line");
        string toText = graph.Node("convert.int-to-string");
        graph.Then(entry, variable).Then(variable, print).Then(print, printMax)
            .Connect(graph.Node("literal.string", "HOME"), "value", variable, "variable")
            .Connect(variable, "result", upper, "instance")
            .Connect(upper, "result", print, "text")
            .Connect(graph.Node("literal.int", "3"), "value", max, "val1")
            .Connect(graph.Node("literal.int", "7"), "value", max, "val2")
            .Connect(max, "result", toText, "value")
            .Connect(toText, "text", printMax, "text");

        Assert.Equal("""
            string getEnvironmentVariable = Environment.GetEnvironmentVariable("HOME") ?? "";
            Console.WriteLine(getEnvironmentVariable.ToUpper());
            Console.WriteLine(Math.Max(3, 7).ToString());
            """, MainBody(graph));
    }

    [Fact]
    public void ReferencedAssembliesContributeTheirPublicMethods()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "Greetings.dll");
        CSharpCompilation library = CSharpCompilation.Create("Greetings",
            [CSharpSyntaxTree.ParseText("""
                namespace Greetings;
                public static class Greeter
                {
                    public static string Greet(string name) => "Hi " + name;
                    public static void Log(string message) { }
                    public static long Unsupported(long value) => value;
                    internal static string Hidden() => "";
                }
                """, cancellationToken: TestContext.Current.CancellationToken)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(library.Emit(path, cancellationToken: TestContext.Current.CancellationToken).Success);

        IReadOnlyList<ExternalMethod> methods = ExternalApi.FromAssemblies([path]).Members;
        Assert.Equal(["Greeter.Greet(string)", "Greeter.Log(string)"], methods.Select(method => method.DisplayName));

        var graph = new GraphBuilder();
        string greet = graph.Node(methods[0].TypeId);
        string print = graph.Node("console.write-line");
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), greet).Then(greet, print)
            .Connect(graph.Node("literal.string", "Ada"), "value", greet, "name")
            .Connect(greet, "result", print, "text");
        var compiler = new GraphCompiler(NodeLibrary.CreateDefault([path]));
        GraphCompilationResult result = compiler.Compile(graph.Graph, new GraphCompilationOptions("Demo"), "Graphs/Test.graph.json");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("using Greetings;", result.Source!.Text, StringComparison.Ordinal);
        Assert.Contains("string greet = Greeter.Greet(\"Ada\");", result.Source.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NodesOfAMissingLibraryAreReportedInsteadOfLost()
    {
        var graph = new GraphBuilder();
        graph.Then(graph.Node(BuiltInNodes.EntryTypeId), graph.Node("dotnet:M:Gone.Thing.Do"));

        Assert.Contains(CodeGenerationTests.Compile(graph).Diagnostics, diagnostic => diagnostic.Id == Compiler.Diagnostics.DiagnosticIds.UnknownNodeType);
    }
}
