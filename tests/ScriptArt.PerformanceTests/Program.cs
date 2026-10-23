using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.NodeEditor;

namespace ScriptArt.PerformanceTests;

/// <summary>
/// Times serialization, compilation, incremental project compilation, snapshots, and spatial queries on synthetic graphs. Each
/// statement is a Print Line fed by a concatenation of two literals, so a graph of N statements has 4N + 1 nodes. Reports the median
/// of several runs after a warm-up.
/// </summary>
/// <remarks>
/// <c>--write-baseline &lt;file&gt;</c> records the results; <c>--check &lt;file&gt;</c> compares against them and exits with 1 when a
/// time exceeds its baseline by more than <see cref="TimeTolerance"/> or an allocation by more than <see cref="AllocationTolerance"/>.
/// Times depend on the machine, so record the baseline on the machine that checks it; allocations do not.
/// </remarks>
internal static class Program
{
    private const int Runs = 7;
    private const double TimeTolerance = 2.0;
    private const double AllocationTolerance = 1.25;

    // Times below this are too noisy to compare; they pass as long as they stay below it.
    private const double NoiseFloorMs = 1.0;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        if (args is ["--write-graph", var count, var path])
        {
            File.WriteAllText(path, GraphSerializer.Serialize(Build(int.Parse(count, CultureInfo.InvariantCulture))));
            return 0;
        }

        Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical CPUs, {(Debugger.IsAttached ? "debugger attached" : "no debugger")}"
            + (IsOptimized() ? "" : ", NOT a Release build: timings are not representative"));
        // The first pass runs every path until the JIT has optimized it; tiered compilation makes earlier timings unrepresentative.
        Measure();
        Dictionary<string, double> results = Measure();
        foreach ((string name, double value) in results)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-40} {value,10:F2} {(IsAllocation(name) ? "MB" : "ms")}"));
        }

        return args switch
        {
            ["--write-baseline", var baseline] => WriteBaseline(baseline, results),
            ["--check", var baseline] => Check(baseline, results),
            [] => 0,
            _ => Usage(),
        };
    }

    private static Dictionary<string, double> Measure()
    {
        var compiler = new GraphCompiler(NodeLibrary.CreateDefault());
        var options = new GraphCompilationOptions("Bench");
        Dictionary<string, double> results = [];
        foreach (int statements in new[] { 250, 2_500 })
        {
            GraphDocument graph = Build(statements);
            string json = GraphSerializer.Serialize(graph);
            string size = graph.Nodes.Count.ToString(CultureInfo.InvariantCulture);
            results[$"serialize {size} nodes"] = Median(() => GraphSerializer.Serialize(graph));
            results[$"deserialize {size} nodes"] = Median(() => GraphSerializer.Deserialize(json));
            results[$"compile+emit {size} nodes"] = Median(() => compiler.Compile(graph, options, "bench"));
            results[$"clone {size} nodes"] = Median(() => graph.Clone());

            long before = GC.GetAllocatedBytesForCurrentThread();
            GraphCompilationResult result = compiler.Compile(graph, options, "bench");
            results[$"allocated per compile {size} nodes"] = (GC.GetAllocatedBytesForCurrentThread() - before) / 1048576.0;
            if (!result.Success)
            {
                throw new InvalidOperationException("Benchmark graph failed to compile.");
            }
        }

        // A project of 20 graphs of 1,001 nodes; one graph changes between incremental compiles.
        GraphDocument[] graphs = [.. Enumerable.Range(0, 20).Select(i => Build(250, $"Graph{i}"))];
        (GraphDocument, string)[] items = [.. graphs.Select(graph => (graph, graph.Name))];
        var projectOptions = new GraphCompilationOptions("Bench", graphs);
        results["project 20x1001 nodes, cold"] = Median(() => new GraphCompilationCache().CompileAll(compiler, items, projectOptions));
        var cache = new GraphCompilationCache();
        cache.CompileAll(compiler, items, projectOptions);
        int edit = 0;
        results["project 20x1001 nodes, one graph changed"] = Median(() =>
        {
            graphs[0].Nodes[3].Properties["value"] = "Edit " + edit++.ToString(CultureInfo.InvariantCulture);
            cache.CompileAll(compiler, items, projectOptions);
        });

        // As the IDE compiles: snapshots in which only the changed graph is a new copy.
        var snapshotCache = new GraphCompilationCache();
        snapshotCache.CompileAll(compiler, items, projectOptions, immutableGraphs: true);
        results["project 20x1001 nodes, one graph changed, snapshot"] = Median(() =>
        {
            GraphDocument changed = items[0].Item1.Clone();
            changed.Nodes[3].Properties["value"] = "Edit " + edit++.ToString(CultureInfo.InvariantCulture);
            items[0] = (changed, items[0].Item2);
            snapshotCache.CompileAll(compiler, items, new GraphCompilationOptions("Bench", [.. items.Select(item => item.Item1)]), immutableGraphs: true);
        });

        // The canvas's index of a 10,001-node graph: building it, and finding what a 1600x900 view at 100% shows.
        GraphDocument large = Build(2_500);
        SpatialIndex<string> index = new();
        results["spatial index build 10001 nodes"] = Median(() =>
        {
            index.Clear();
            foreach (GraphNode node in large.Nodes)
            {
                index.Set(node.Id, new Area(node.X, node.Y, 180, 80));
            }
        });
        results["spatial query 1600x900 view"] = Median(() => index.Query(new Area(50_000, 0, 1600, 900)));
        return results;
    }

    private static int WriteBaseline(string path, Dictionary<string, double> results)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(results, Indented) + "\n");
        Console.WriteLine($"Baseline written to {path}.");
        return 0;
    }

    private static int Check(string path, Dictionary<string, double> results)
    {
        Dictionary<string, double> baseline = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) ?? [];
        int failures = 0;
        foreach ((string name, double value) in results)
        {
            if (!baseline.TryGetValue(name, out double expected))
            {
                Console.WriteLine($"NEW   {name}: no baseline");
                continue;
            }

            double tolerance = IsAllocation(name) ? AllocationTolerance : TimeTolerance;
            bool regressed = value > expected * tolerance && (IsAllocation(name) || value > NoiseFloorMs);
            failures += regressed ? 1 : 0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(regressed ? "SLOWER" : "ok"),-6} {name}: {value:F2} vs {expected:F2} (limit x{tolerance})"));
        }

        Console.WriteLine(failures == 0 ? "No performance regressions." : $"{failures} measurement(s) regressed.");
        return failures == 0 ? 0 : 1;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: ScriptArt.PerformanceTests [--write-baseline <file> | --check <file> | --write-graph <statements> <file>]");
        return 2;
    }

    private static bool IsAllocation(string name) => name.StartsWith("allocated", StringComparison.Ordinal);

    private static bool IsOptimized() =>
        typeof(Program).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    private static GraphDocument Build(int statements, string name = "Program")
    {
        GraphDocument graph = GraphDocument.CreateEmpty(name, GraphKind.EntryPoint);
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
