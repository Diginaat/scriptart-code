using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;
using ScriptArt.ProjectSystem.Debugging;

namespace ScriptArt.Tests;

/// <summary>The Debug Adapter Protocol framing and connection, and the mapping between node breakpoints and generated lines.</summary>
public sealed class DebuggingTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    [Fact]
    public async Task MessagesRoundTripThroughContentLengthFraming()
    {
        using var stream = new MemoryStream();
        await DebugAdapterConnection.WriteMessageAsync(stream, new JsonObject { ["text"] = "héllo" }, TestContext.Current.CancellationToken);
        await DebugAdapterConnection.WriteMessageAsync(stream, new JsonObject { ["n"] = 2 }, TestContext.Current.CancellationToken);
        stream.Position = 0;

        string framed = Encoding.UTF8.GetString(stream.ToArray());
        int bodyLength = framed.IndexOf("Content-Length", 1, StringComparison.Ordinal) - framed.IndexOf('{', StringComparison.Ordinal);
        Assert.StartsWith($"Content-Length: {bodyLength}\r\n\r\n{{\"text\":", framed, StringComparison.Ordinal);
        Assert.Equal("héllo", (string?)(await DebugAdapterConnection.ReadMessageAsync(stream, TestContext.Current.CancellationToken))!["text"]);
        Assert.Equal(2, (int?)(await DebugAdapterConnection.ReadMessageAsync(stream, TestContext.Current.CancellationToken))!["n"]);
        Assert.Null(await DebugAdapterConnection.ReadMessageAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MessageWithoutContentLengthIsRejected()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Type: x\r\n\r\n{}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => DebugAdapterConnection.ReadMessageAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestsGetTheirResponsesAndEventsAreRaised()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterInput = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientInput = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var connection = new DebugAdapterConnection(clientInput, toAdapter);
        var events = new List<string>();
        connection.EventReceived += (name, body) => events.Add($"{name}:{body?["reason"]}");
        connection.Start();

        Task<JsonNode?> threads = connection.RequestAsync("threads", null, cancellation);
        Task<JsonNode?> refused = connection.RequestAsync("pause", new JsonObject { ["threadId"] = 1 }, cancellation);
        JsonNode first = (await DebugAdapterConnection.ReadMessageAsync(adapterInput, cancellation))!;
        JsonNode second = (await DebugAdapterConnection.ReadMessageAsync(adapterInput, cancellation))!;
        Assert.Equal("threads", (string?)first["command"]);
        Assert.Equal(1, (int?)second["arguments"]!["threadId"]);

        await DebugAdapterConnection.WriteMessageAsync(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject { ["reason"] = "breakpoint" } }, cancellation);
        await DebugAdapterConnection.WriteMessageAsync(fromAdapter, new JsonObject
        {
            ["type"] = "response", ["request_seq"] = (int)second["seq"]!, ["success"] = false, ["command"] = "pause", ["message"] = "Not now.",
        }, cancellation);
        await DebugAdapterConnection.WriteMessageAsync(fromAdapter, new JsonObject
        {
            ["type"] = "response", ["request_seq"] = (int)first["seq"]!, ["success"] = true, ["body"] = new JsonObject { ["threads"] = new JsonArray() },
        }, cancellation);

        Assert.NotNull((await threads)!["threads"]);
        Assert.Equal("Not now.", (await Assert.ThrowsAsync<DebugAdapterException>(() => refused)).Message);
        Assert.Equal(["stopped:breakpoint"], events);

        Task<JsonNode?> unanswered = connection.RequestAsync("continue", null, cancellation);
        fromAdapter.Dispose();
        await Assert.ThrowsAsync<DebugAdapterException>(() => unanswered);
    }

    [Fact]
    public void NodeBreakpointsMapToTheFirstGeneratedLineAndBack()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        ProjectGraph program = project.Graphs[0];
        GraphNode print = program.Graph.Nodes.Single(node => node.Type == "console.write-line");
        GraphNode text = program.Graph.Nodes.Single(node => node.Type == "literal.string");
        ProjectGenerationResult generated = project.Generate(Compiler);
        var map = new DebugSourceMap(project, generated.Sources);
        string file = Path.Combine(project.GeneratedDirectory, "Program.g.cs");
        int line = Array.IndexOf(File.ReadAllLines(file), "        Console.WriteLine(\"Hello, World!\");") + 1;

        Assert.Equal(new SourceLocation(file, line), map.LocationOf(new NodeBreakpoint(program.Graph.Id, print.Id)));
        Assert.Null(map.LocationOf(new NodeBreakpoint(program.Graph.Id, text.Id)));
        Assert.Null(map.LocationOf(new NodeBreakpoint("gone", print.Id)));
        Assert.Equal([line], map.Lines([new NodeBreakpoint(program.Graph.Id, print.Id), new NodeBreakpoint(program.Graph.Id, text.Id)])[file]);
        Assert.Equal((program, print.Id), map.NodeAt(file, line));
        Assert.Equal((program, (string?)null), map.NodeAt(file, 1));
        Assert.Null(map.NodeAt(Path.Combine(directory.Path, "Other.cs"), 1));
    }

    [Fact]
    public void OutputAssemblyFollowsTheProjectFile()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");

        Assert.Equal(Path.Combine(directory.Path, "bin", "Debug", "net10.0", "Hello.dll"), project.OutputAssemblyPath("Debug"));

        File.WriteAllText(project.CsprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>Hi</AssemblyName></PropertyGroup></Project>");
        Assert.Equal(Path.Combine(directory.Path, "bin", "Release", "net11.0", "Hi.dll"), project.OutputAssemblyPath("Release"));
    }
}
