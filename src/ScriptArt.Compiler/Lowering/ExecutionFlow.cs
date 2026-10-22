using ScriptArt.Compiler.Diagnostics;

namespace ScriptArt.Compiler.Lowering;

/// <summary>
/// Control-flow analysis of the execution connections reachable from an entry node:
/// reachability, cycle detection, and immediate post-dominators.
/// </summary>
/// <remarks>
/// The immediate post-dominator of a node is the first node that every path leaving it must pass through.
/// For a linear node it is simply the next node; for a branch it is where the arms reconverge, which is where
/// the generated <c>if</c> statement ends. All traversals are iterative so very long chains cannot overflow the stack.
/// A nested block (a loop body, or a Try node's blocks) is not a successor of its node: it is lowered as a nested region, and
/// where its flow ends the block ends. The search still follows body connections, so body nodes are reachable and a wire from the body back
/// to an earlier node is reported as a cycle.
/// </remarks>
internal sealed class ExecutionFlow
{
    private readonly Dictionary<string, int> indexById;
    private readonly ResolvedNode[] nodes;
    private readonly int[] immediatePostDominator;

    private ExecutionFlow(ResolvedNode[] nodes, Dictionary<string, int> indexById, int[] immediatePostDominator)
    {
        this.nodes = nodes;
        this.indexById = indexById;
        this.immediatePostDominator = immediatePostDominator;
    }

    public bool IsReachable(string nodeId) => indexById.ContainsKey(nodeId);

    /// <summary>The node where control continues after <paramref name="node"/>, or null when control leaves the method.</summary>
    public ResolvedNode? ImmediatePostDominator(ResolvedNode node)
    {
        int dominator = immediatePostDominator[indexById[node.Id]];
        return dominator == nodes.Length ? null : nodes[dominator];
    }

    /// <summary>
    /// Analyzes the flow from <paramref name="entry"/>. Returns null after reporting a diagnostic when the flow contains a cycle.
    /// </summary>
    public static ExecutionFlow? Analyze(GraphIndex index, ResolvedNode entry, List<GraphDiagnostic> diagnostics)
    {
        List<ResolvedNode> order = [];
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        List<List<int>> successors = [];

        // Nodes with an unconnected execution output: leaving through that output ends the method.
        // Nodes without execution outputs (Return) always end it.
        List<bool> exitsMethod = [];

        // Depth-first search from the entry: numbers reachable nodes and finds back edges (cycles).
        var onStack = new HashSet<int>();
        var stack = new Stack<(int Node, IEnumerator<(ResolvedNode Target, bool LoopBody)> Successors)>();
        int Visit(ResolvedNode node)
        {
            int id = order.Count;
            order.Add(node);
            indexById.Add(node.Id, id);
            successors.Add([]);
            exitsMethod.Add(node.Definition.Pins.Any(pin =>
                pin.IsExecution && pin.Direction == Nodes.PinDirection.Output && !pin.StartsBlock && index.ExecutionTarget(node.Id, pin.Id) is null));
            onStack.Add(id);
            stack.Push((id, Successors(node).GetEnumerator()));
            return id;
        }

        IEnumerable<(ResolvedNode Target, bool LoopBody)> Successors(ResolvedNode node)
        {
            foreach (Nodes.PinDefinition pin in node.Definition.Pins)
            {
                if (pin.IsExecution && pin.Direction == Nodes.PinDirection.Output && index.ExecutionTarget(node.Id, pin.Id) is { } target)
                {
                    yield return (target, pin.StartsBlock);
                }
            }
        }

        Visit(entry);
        while (stack.Count > 0)
        {
            (int current, IEnumerator<(ResolvedNode Target, bool LoopBody)> next) = stack.Peek();
            if (!next.MoveNext())
            {
                next.Dispose();
                stack.Pop();
                onStack.Remove(current);
                continue;
            }

            (ResolvedNode target, bool loopBody) = next.Current;
            if (indexById.TryGetValue(target.Id, out int targetId))
            {
                if (onStack.Contains(targetId))
                {
                    diagnostics.Add(new(DiagnosticIds.ExecutionCycle, DiagnosticSeverity.Error,
                        "Execution flow loops back to an earlier node. Use a While Loop or For Loop node to repeat statements.", index.Graph.Id, target.Id));
                    foreach ((_, IEnumerator<(ResolvedNode Target, bool LoopBody)> pending) in stack)
                    {
                        pending.Dispose();
                    }

                    return null;
                }

                if (!loopBody)
                {
                    successors[current].Add(targetId);
                }

                continue;
            }

            int visited = Visit(target);
            if (!loopBody)
            {
                successors[current].Add(visited);
            }
        }

        ResolvedNode[] nodes = [.. order];
        return new ExecutionFlow(nodes, indexById, ComputePostDominators(successors, exitsMethod));
    }

    /// <summary>
    /// Cooper–Harvey–Kennedy dominator algorithm on the reversed flow graph, with a virtual exit node
    /// (index <c>successors.Count</c>) following every node that has an unconnected execution output.
    /// </summary>
    private static int[] ComputePostDominators(List<List<int>> successors, List<bool> exitsMethod)
    {
        int count = successors.Count;
        int exit = count;
        var predecessors = new List<int>[count + 1];
        for (int i = 0; i <= count; i++)
        {
            predecessors[i] = [];
        }

        for (int node = 0; node < count; node++)
        {
            if (exitsMethod[node] || successors[node].Count == 0)
            {
                successors[node].Add(exit);
            }

            foreach (int successor in successors[node])
            {
                predecessors[successor].Add(node);
            }
        }

        // Post-order of the reversed graph (edges run from a node to its predecessors), starting at the exit.
        int[] postOrder = new int[count + 1];
        var reversePostOrder = new List<int>(count + 1);
        var visited = new bool[count + 1];
        var stack = new Stack<(int Node, int NextPredecessor)>();
        stack.Push((exit, 0));
        visited[exit] = true;
        while (stack.Count > 0)
        {
            (int node, int next) = stack.Pop();
            if (next < predecessors[node].Count)
            {
                stack.Push((node, next + 1));
                int predecessor = predecessors[node][next];
                if (!visited[predecessor])
                {
                    visited[predecessor] = true;
                    stack.Push((predecessor, 0));
                }

                continue;
            }

            postOrder[node] = reversePostOrder.Count;
            reversePostOrder.Add(node);
        }

        reversePostOrder.Reverse();

        int[] dominator = new int[count + 1];
        Array.Fill(dominator, -1);
        dominator[exit] = exit;

        int Intersect(int a, int b)
        {
            while (a != b)
            {
                while (postOrder[a] < postOrder[b])
                {
                    a = dominator[a];
                }

                while (postOrder[b] < postOrder[a])
                {
                    b = dominator[b];
                }
            }

            return a;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int node in reversePostOrder)
            {
                if (node == exit)
                {
                    continue;
                }

                int candidate = -1;
                foreach (int successor in successors[node])
                {
                    if (dominator[successor] != -1)
                    {
                        candidate = candidate == -1 ? successor : Intersect(successor, candidate);
                    }
                }

                if (dominator[node] != candidate)
                {
                    dominator[node] = candidate;
                    changed = true;
                }
            }
        }

        return dominator;
    }
}
