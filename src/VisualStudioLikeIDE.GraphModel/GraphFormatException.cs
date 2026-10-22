namespace VisualStudioLikeIDE.GraphModel;

/// <summary>Raised when a graph or project file cannot be read.</summary>
public sealed class GraphFormatException : Exception
{
    public GraphFormatException(string message)
        : base(message)
    {
    }

    public GraphFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
