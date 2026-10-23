using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;

namespace ScriptArt.Compiler;

/// <summary>
/// Compiles the graphs of a project, reusing the result of each graph whose content did not change since it was last compiled, as
/// long as nothing else it depends on changed either (D-039).
/// </summary>
/// <remarks>
/// A graph's code depends on its own content and on the other graphs only through their signatures: names, kinds, owners,
/// parameters, return types, properties, type parameters, and which graphs are async. The cache key is therefore the graph's
/// serialized content plus one fingerprint of every signature, the options, and the library's <see cref="NodeLibrary.Version"/>;
/// changing a signature recompiles every graph, changing a graph's nodes recompiles only that graph. Safe for concurrent use.
/// </remarks>
public sealed class GraphCompilationCache
{
    private readonly ConcurrentDictionary<string, (string Key, GraphCompilationResult Result)> results = new(StringComparer.Ordinal);

    // Content hashes of graphs the caller promised never to change, such as snapshot copies, by identity.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GraphDocument, string> immutableHashes = [];
    private NodeLibrary? library;
    private int compiled;
    private int reused;

    /// <summary>How many graphs were compiled, rather than reused, since the cache was created.</summary>
    public int CompiledCount => compiled;

    /// <summary>How many graph results were reused since the cache was created.</summary>
    public int ReusedCount => reused;

    /// <summary>Compiles each of <paramref name="graphs"/> with <paramref name="options"/>, reusing unchanged results.</summary>
    /// <param name="graphs">Each graph with the description its generated header names, such as its file path.</param>
    /// <param name="immutableGraphs">
    /// True when none of <paramref name="graphs"/> will ever change, as for snapshot copies: a graph's content is then hashed once
    /// per object rather than on every call.
    /// </param>
    public IReadOnlyList<GraphCompilationResult> CompileAll(GraphCompiler compiler, IReadOnlyList<(GraphDocument Graph, string SourceDescription)> graphs,
        GraphCompilationOptions options, bool immutableGraphs = false)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.Exchange(ref library, compiler.Library) != compiler.Library)
        {
            results.Clear();
        }

        options = options with { AsyncGraphIds = options.AsyncGraphIds ?? compiler.AsyncGraphs(options.Graphs) };
        string context = ContextFingerprint(compiler, options);
        var compiledResults = new GraphCompilationResult[graphs.Count];
        Parallel.For(0, graphs.Count, i =>
        {
            (GraphDocument graph, string description) = graphs[i];
            string content = immutableGraphs
                ? immutableHashes.GetValue(graph, copy => Hash(GraphSerializer.Serialize(copy)))
                : Hash(GraphSerializer.Serialize(graph));
            string key = context + "|" + description + "|" + content;
            if (results.TryGetValue(graph.Id, out var cached) && cached.Key == key)
            {
                Interlocked.Increment(ref reused);
                compiledResults[i] = cached.Result;
                return;
            }

            GraphCompilationResult result = compiler.Compile(graph, options, description);
            results[graph.Id] = (key, result);
            Interlocked.Increment(ref compiled);
            compiledResults[i] = result;
        });

        // Results of graphs that are gone would otherwise stay until the cache is dropped.
        HashSet<string> ids = [.. graphs.Select(item => item.Graph.Id)];
        foreach (string stale in results.Keys.Where(id => !ids.Contains(id)))
        {
            results.TryRemove(stale, out _);
        }

        return compiledResults;
    }

    // Everything a graph's code depends on besides its own content.
    private static string ContextFingerprint(GraphCompiler compiler, GraphCompilationOptions options)
    {
        var text = new StringBuilder();
        text.Append(options.RootNamespace).Append('\u001f').Append(options.HostClass).Append('\u001f').Append(compiler.Library.Version).Append('\u001e');
        foreach (GraphDocument graph in options.Graphs)
        {
            text.AppendJoin('\u001f', graph.Id, graph.Name, graph.Kind, graph.Owner, graph.ReturnType).Append('\u001f');
            foreach (GraphParameter parameter in graph.Parameters)
            {
                text.AppendJoin('\u001f', parameter.Id, parameter.Name, parameter.Type).Append('\u001f');
            }

            foreach (GraphProperty property in graph.Properties)
            {
                text.AppendJoin('\u001f', property.Id, property.Name, property.Type, property.Value).Append('\u001f');
            }

            text.AppendJoin('\u001f', graph.TypeParameters).Append('\u001e');
        }

        text.AppendJoin('\u001f', options.AsyncGraphIds!.Order(StringComparer.Ordinal));
        return Hash(text.ToString());
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
