using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;
using ScriptArt.ProjectSystem.Debugging;

namespace ScriptArt.App;

/// <summary>
/// Debugging with netcoredbg: breakpoints on nodes (F9), Start Debugging (F5), stepping (F10, F11, Shift+F11), and the Debug tab's
/// call stack and locals. A stop in generated code selects the node that produced the line and outlines it on the graph.
/// </summary>
public partial class MainWindow
{
    private DebugSession? debugSession;
    private DebugSourceMap? debugMap;
    private DebugStop? debugStop;

    // The node the program is stopped at, by graph, so the outline follows when another graph is opened.
    private (string GraphId, string NodeId)? executionNode;

    private void InitializeDebugView()
    {
        CallStackList.SelectionChanged += (_, _) =>
        {
            if (CallStackList.SelectedItem is ListBoxItem { Tag: DebugFrame frame })
            {
                ShowFrame(frame);
            }
        };
    }

    private string? ExecutionNodeIn(ProjectGraph graph) =>
        executionNode is { } node && node.GraphId == graph.Graph.Id ? node.NodeId : null;

    private async void OnStartDebugging(object? sender, RoutedEventArgs e)
    {
        if (workspace is null || busy || runningProgram is not null || debugSession is not null)
        {
            return;
        }

        if (DebuggerLocator.Find() is not { } debugger)
        {
            BottomTabs.SelectedItem = OutputTab;
            AppendOutput($"Debugging needs netcoredbg, the open-source .NET debugger. Install it from https://github.com/Samsung/netcoredbg/releases and put it on PATH, "
                + $"in {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptArtCode", "netcoredbg")}, "
                + $"or set {DebuggerLocator.PathVariable} to its path. Ctrl+F5 runs without debugging.");
            SetStatus("netcoredbg was not found. See Output.");
            return;
        }

        if (!await BuildAsync())
        {
            return;
        }

        VisualProject project = workspace.Project;
        var map = new DebugSourceMap(project, workspace.GeneratedSources);
        foreach (NodeBreakpoint breakpoint in workspace.Breakpoints.Where(breakpoint => map.LocationOf(breakpoint) is null))
        {
            AppendOutput($"A breakpoint on node {breakpoint.NodeId} is not hit: the node generates no statement of its own.");
        }

        string program = project.OutputAssemblyPath(Configuration);
        AppendOutput($"> debugging {program} with {debugger}");
        SetBusy(true, "Starting the debugger…");
        DebugSession session;
        try
        {
            session = await DebugSession.StartAsync(debugger, program, map.Lines(workspace.Breakpoints),
                line => Dispatcher.UIThread.Post(() => AppendOutput(line)), CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DebugAdapterException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            SetBusy(false, null);
            ReportError("Could not start debugging", exception);
            return;
        }

        debugSession = session;
        debugMap = map;
        session.Stopped += stop => Dispatcher.UIThread.Post(() => OnDebugStopped(session, stop));
        session.Continued += () => Dispatcher.UIThread.Post(() => OnDebugContinued(session));
        SetBusy(false, "Debugging. Type input below the output and press Enter; F5 continues after a breakpoint.");
        DebugHint.IsVisible = false;
        InputBox.Focus();

        int? exitCode = null;
        try
        {
            exitCode = await session.WaitForExitAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // Stopped from the IDE; the process is already gone.
        }

        if (debugSession == session)
        {
            AppendOutput($"Program exited with code {exitCode}.");
            SetStatus($"Program exited with code {exitCode}.");
            EndDebugging();
        }
    }

    /// <summary>Ends the debug session, if any, and clears what it showed.</summary>
    private void EndDebugging()
    {
        if (debugSession is not { } session)
        {
            return;
        }

        debugSession = null;
        debugMap = null;
        ClearStop();
        DebugHint.IsVisible = true;
        _ = session.DisposeAsync().AsTask();
        UpdateCommandStates();
    }

    private void ClearStop()
    {
        debugStop = null;
        executionNode = null;
        GraphCanvas.ExecutionNodeId = null;
        CallStackList.Items.Clear();
        LocalsTree.Items.Clear();
    }

    private async void OnDebugStopped(DebugSession session, DebugStop stop)
    {
        if (session != debugSession)
        {
            return;
        }

        debugStop = stop;
        UpdateCommandStates();
        BottomTabs.SelectedItem = DebugTab;
        CallStackList.Items.Clear();
        IReadOnlyList<DebugFrame> frames;
        try
        {
            frames = await session.StackTraceAsync(stop.ThreadId);
        }
        catch (DebugAdapterException exception)
        {
            SetStatus($"Paused, but the call stack could not be read: {exception.Message}");
            return;
        }

        if (session != debugSession || debugStop != stop)
        {
            return;
        }

        foreach (DebugFrame frame in frames)
        {
            var row = new DockPanel();
            var location = new TextBlock { Text = Describe(frame), Classes = { "secondary" }, Margin = new Avalonia.Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(location, Dock.Right);
            row.Children.Add(location);
            row.Children.Add(new TextBlock { Text = frame.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            CallStackList.Items.Add(new ListBoxItem { Content = row, Tag = frame });
        }

        string reason = stop.Reason == "exception" && stop.Description is { } description ? $"on an exception: {description}" : $"({stop.Reason})";
        SetStatus($"Paused {reason}. F5 continues, F10 steps over, F11 steps into, Shift+F11 steps out.");
        CallStackList.SelectedIndex = frames.Count > 0 ? 0 : -1;
    }

    private void OnDebugContinued(DebugSession session)
    {
        if (session == debugSession)
        {
            ClearStop();
            SetStatus("Running…");
            UpdateCommandStates();
        }
    }

    // Where a frame is: the graph and node for generated code, the file and line otherwise.
    private string Describe(DebugFrame frame)
    {
        if (frame.Path is null)
        {
            return "no source";
        }

        if (debugMap?.NodeAt(frame.Path, frame.Line) is ({ } graph, var nodeId))
        {
            string? node = nodeId is null ? null : graph.Graph.FindNode(nodeId) is { } found && workspace?.Compiler.Library.Resolve(found, graph.Graph, workspace.Project.GraphDocuments) is { } definition
                ? definition.DisplayName
                : null;
            return node is null ? $"{graph.Graph.Name} graph" : $"{graph.Graph.Name}: {node}";
        }

        return $"{Path.GetFileName(frame.Path)}({frame.Line})";
    }

    /// <summary>Shows where a frame is, on its graph or in its file, and its local variables.</summary>
    private async void ShowFrame(DebugFrame frame)
    {
        if (workspace is null || debugSession is not { } session)
        {
            return;
        }

        executionNode = null;
        if (frame.Path is { } path && debugMap?.NodeAt(path, frame.Line) is ({ } graph, var nodeId))
        {
            if (graph != workspace.Graph && workspace.Project.Graphs.Contains(graph))
            {
                OpenProjectGraph(graph);
            }

            ShowGeneratedCode();
            if (nodeId is not null)
            {
                executionNode = (graph.Graph.Id, nodeId);
                GraphCanvas.Select(nodeId, frame: true);
            }
            else if (frame.Line <= generatedDocument.LineCount)
            {
                CodeEditor.ScrollToLine(frame.Line);
            }
        }
        else if (frame.Path is { } file && File.Exists(file))
        {
            ShowCodeFile(file, frame.Line);
            BottomTabs.SelectedItem = DebugTab;
        }

        GraphCanvas.ExecutionNodeId = ExecutionNodeIn(workspace.Graph);
        LocalsTree.Items.Clear();
        try
        {
            foreach (DebugVariable variable in await session.LocalsAsync(frame.Id))
            {
                LocalsTree.Items.Add(VariableItem(variable));
            }
        }
        catch (DebugAdapterException exception)
        {
            SetStatus($"The local variables could not be read: {exception.Message}");
        }
    }

    // A variable row; one with members loads them when first expanded.
    private TreeViewItem VariableItem(DebugVariable variable)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = variable.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        header.Children.Add(new TextBlock { Text = variable.Value });
        if (variable.Type is { Length: > 0 } type)
        {
            header.Children.Add(new TextBlock { Text = type, Classes = { "secondary" } });
        }

        var item = new TreeViewItem { Header = header, Tag = variable };
        if (variable.ChildrenReference == 0)
        {
            return item;
        }

        item.Items.Add(new TreeViewItem { Header = "…" });
        bool loaded = false;
        item.PropertyChanged += async (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty || !item.IsExpanded || loaded || debugSession is not { } session)
            {
                return;
            }

            loaded = true;
            try
            {
                IReadOnlyList<DebugVariable> members = await session.ChildrenAsync(variable.ChildrenReference);
                item.Items.Clear();
                foreach (DebugVariable member in members)
                {
                    item.Items.Add(VariableItem(member));
                }
            }
            catch (DebugAdapterException exception)
            {
                SetStatus($"The members of {variable.Name} could not be read: {exception.Message}");
            }
        };
        return item;
    }

    private void OnContinue(object? sender, RoutedEventArgs e) => DebugCommand(session => session.ContinueAsync());

    private void OnStepOver(object? sender, RoutedEventArgs e) => DebugCommand(session => session.StepOverAsync());

    private void OnStepInto(object? sender, RoutedEventArgs e) => DebugCommand(session => session.StepIntoAsync());

    private void OnStepOut(object? sender, RoutedEventArgs e) => DebugCommand(session => session.StepOutAsync());

    private async void OnPause(object? sender, RoutedEventArgs e)
    {
        if (debugSession is { } session && debugStop is null)
        {
            try
            {
                await session.PauseAsync();
            }
            catch (DebugAdapterException exception)
            {
                SetStatus($"Could not pause: {exception.Message}");
            }
        }
    }

    // Runs a command that resumes a paused program.
    private async void DebugCommand(Func<DebugSession, Task> command)
    {
        if (debugSession is not { } session || debugStop is null)
        {
            return;
        }

        try
        {
            await command(session);
        }
        catch (DebugAdapterException exception)
        {
            SetStatus($"The debugger refused: {exception.Message}");
        }
    }

    /// <summary>Adds or removes breakpoints on the selected nodes; while debugging, the debugger gets the change at once.</summary>
    private async void OnToggleBreakpoint(object? sender, RoutedEventArgs e)
    {
        if (workspace is null)
        {
            return;
        }

        GraphNode[] nodes = [.. SelectedNodes()];
        if (nodes.Length == 0)
        {
            SetStatus("Select a node to put a breakpoint on.");
            return;
        }

        bool added = false;
        foreach (GraphNode node in nodes)
        {
            added |= workspace.ToggleBreakpoint(node.Id);
        }

        GraphCanvas.Breakpoints = workspace.OpenGraphBreakpoints;
        UpdateCommandStates();
        bool inline = workspace.LastCompilation.Source is { } source && nodes.Any(node => source.FindNode(node.Id) is null && workspace.OpenGraphBreakpoints.Contains(node.Id));
        SetStatus(!added ? "Breakpoint removed."
            : inline ? "Breakpoint set, but this node generates no statement of its own: its value is used inline. Put the breakpoint on the node that uses it."
            : "Breakpoint set. The program stops before this node runs when debugging (F5).");
        await UpdateDebuggerBreakpointsAsync(workspace.Graph);
    }

    private async void OnClearBreakpoints(object? sender, RoutedEventArgs e)
    {
        if (workspace is null)
        {
            return;
        }

        ProjectGraph[] graphs = [.. workspace.Project.Graphs.Where(graph => workspace.Breakpoints.Any(breakpoint => breakpoint.GraphId == graph.Graph.Id))];
        workspace.ClearBreakpoints();
        GraphCanvas.Breakpoints = workspace.OpenGraphBreakpoints;
        UpdateCommandStates();
        SetStatus("Deleted all breakpoints.");
        foreach (ProjectGraph graph in graphs)
        {
            await UpdateDebuggerBreakpointsAsync(graph);
        }
    }

    // Sends the breakpoints of one graph's generated file to the running debugger.
    private async Task UpdateDebuggerBreakpointsAsync(ProjectGraph graph)
    {
        if (debugSession is not { } session || debugMap is not { } map || workspace is null)
        {
            return;
        }

        string path = Path.GetFullPath(workspace.Project.GeneratedPath(graph.Graph));
        try
        {
            await session.SetBreakpointsAsync(path, map.Lines(workspace.Breakpoints).GetValueOrDefault(path) ?? []);
        }
        catch (DebugAdapterException exception)
        {
            SetStatus($"The debugger did not take the breakpoint: {exception.Message}");
        }
    }
}
