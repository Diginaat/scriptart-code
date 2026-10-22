using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A compiler message linked to the graph element that caused it.
/// </summary>
/// <param name="Id">Stable code such as <c>VSG0005</c>, or a Roslyn code such as <c>CS0103</c>.</param>
/// <param name="NodeId">The node the diagnostic refers to, when there is one.</param>
/// <param name="Pin">The pin the diagnostic refers to, when there is one.</param>
/// <param name="FilePath">The generated or handwritten file the diagnostic refers to, for Roslyn diagnostics.</param>
/// <param name="Line">One-based line in <paramref name="FilePath"/>.</param>
public sealed record GraphDiagnostic(
    string Id,
    DiagnosticSeverity Severity,
    string Message,
    string? GraphId = null,
    string? NodeId = null,
    PinRef? Pin = null,
    string? FilePath = null,
    int? Line = null)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString() => $"{Severity.ToString().ToUpperInvariant()} {Id}: {Message}";
}

/// <summary>
/// Diagnostic codes produced by the graph compiler. Codes are stable and documented in docs/COMPILER_PIPELINE.md.
/// </summary>
public static class DiagnosticIds
{
    public const string UnknownNodeType = "VSG0001";
    public const string DuplicateNodeId = "VSG0002";
    public const string DanglingConnection = "VSG0003";
    public const string InvalidConnection = "VSG0004";
    public const string TypeMismatch = "VSG0005";
    public const string MultipleInputConnections = "VSG0006";
    public const string MultipleExecutionTargets = "VSG0007";
    public const string MissingEntry = "VSG0008";
    public const string MultipleEntries = "VSG0009";
    public const string UnconnectedInput = "VSG0010";
    public const string ExecutionCycle = "VSG0011";
    public const string DataCycle = "VSG0012";
    public const string ValueNotAvailable = "VSG0013";
    public const string UnreachableNode = "VSG0014";
    public const string UnusedNode = "VSG0015";
    public const string InvalidPropertyValue = "VSG0016";
    public const string UnstructuredFlow = "VSG0017";
    public const string InvalidIdentifier = "VSG0018";
    public const string DuplicateConnection = "VSG0019";
    public const string UnsupportedGraphKind = "VSG0020";
    public const string UnknownVariable = "VSG0021";
    public const string InvalidVariable = "VSG0022";
    public const string VariableNeverRead = "VSG0023";
    public const string JumpOutsideLoop = "VSG0024";
    public const string UnknownFunction = "VSG0025";
    public const string MissingReturn = "VSG0026";
    public const string InvalidFunction = "VSG0027";
    public const string JumpOutOfFinally = "VSG0028";
    public const string UnknownType = "VSG0029";
    public const string InvalidType = "VSG0030";
}
