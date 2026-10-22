using System.Diagnostics;
using System.Globalization;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.PerformanceTests;

/// <summary>
/// Times serialization and compilation of synthetic graphs. Each statement is a Print Line fed by a concatenation of two
/// literals, so a graph of N statements has 4N + 1 nodes. Reports the median of several runs after a warm-up.
/// </summary>
internal static class Program
{
    private const int Runs = 7;

    /// <summary>Runs the timings, or with <c>--write-graph &lt;statements&gt; &lt;path&gt;</c> writes a synthetic graph for measuring the editor by hand.</summary>
    public static void Main(string[] args)
    {
        if (args is ["--write-graph", var count, var path])
        {
            File.WriteAllText(path, GraphSerializer.Serialize(Build(int.Parse(count, CultureInfo.InvariantCulture))));
            return;
        }

        var compiler = new GraphCompiler(NodeLibrary.CreateDefault());
        var options = new GraphCompilationOptions("Bench");
        Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical CPUs, {(Debugger.IsAttached ? "debugger attached" : "no debugger")}");
        Console.WriteLine("nodes   | serialize | deserialize | compile+emit | alloc/compile");

        foreach (int statements in new[] { 250, 2_500 })
        {
            GraphDocument graph = Build(statements);
            string json = GraphSerializer.Serialize(graph);
            double serialize = Median(() => GraphSerializer.Serialize(graph));
            double deserialize = Median(() => GraphSerializer.Deserialize(json));
            double compile = Median(() => compiler.Compile(graph, options, "bench"));

            long before = GC.GetAllocatedBytesForCurrentThread();
            GraphCompilationResult result = compiler.Compile(graph, options, "bench");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (!result.Success)
            {
                throw new InvalidOperationException("Benchmark graph failed to compile.");
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{graph.Nodes.Count,7} | {serialize,6:F1} ms | {deserialize,8:F1} ms | {compile,9:F1} ms | {allocated / 1048576.0,8:F1} MB"));
        }
    }

    private static GraphDocument Build(int statements)
    {
        GraphDocument graph = GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint);
        var entry = new GraphNode { Id = "entry", Type = BuiltInNodes.EntryTypeId };
        graph.Nodes.Add(entry);
        string previous = entry.Id;
        for (int i = 0; i < statements; i++)
        {
            string id = i.ToString(CultureInfo.InvariantCulture);
            var print = new GraphNode { Id = "p" + id, Type = "console.write-line", X = i * 220 };
            var concat = new GraphNode { Id = "c" + id, Type = "string.concat", X = i * 220, Y = 120 };
            var left = new GraphNode { Id = "l" + id, Type = "literal.string", X = i * 220, Y = 240 };
            var right = new GraphNode { Id = "r" + id, Type = "literal.string", X = i * 220, Y = 320 };
            left.Properties["value"] = "Line ";
            right.Properties["value"] = id;
            graph.Nodes.AddRange([print, concat, left, right]);
            graph.Connections.Add(new(new(previous, "then"), new(print.Id, "exec")));
            graph.Connections.Add(new(new(concat.Id, "result"), new(print.Id, "text")));
            graph.Connections.Add(new(new(left.Id, "value"), new(concat.Id, "a")));
            graph.Connections.Add(new(new(right.Id, "value"), new(concat.Id, "b")));
            previous = print.Id;
        }

        return graph;
    }

    private static double Median(Action action)
    {
        action();
        double[] times = new double[Runs];
        for (int i = 0; i < Runs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        return times[Runs / 2];
    }
}
