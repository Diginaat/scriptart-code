using Avalonia;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.App.Canvas;

/// <summary>The position of one pin in world coordinates.</summary>
internal readonly record struct PinLayout(PinDefinition Pin, Point Center);

/// <summary>
/// Geometry of a node in world coordinates, derived from its definition. Layouts are view state:
/// they are rebuilt from the graph and never persisted.
/// </summary>
internal sealed class NodeLayout
{
    /// <summary>Distance of pin centers from the node's left and right edges.</summary>
    public const double PinInset = 12;

    private NodeLayout(GraphNode node, NodeDefinition definition, double width, string? preview, List<PinLayout> pins, double height)
    {
        Node = node;
        Definition = definition;
        Width = width;
        Preview = preview;
        Pins = pins;
        Height = height;
    }

    public GraphNode Node { get; }

    public NodeDefinition Definition { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>Text shown under the header, such as a literal's value.</summary>
    public string? Preview { get; }

    public IReadOnlyList<PinLayout> Pins { get; }

    public Rect Bounds => new(Node.X, Node.Y, Width, Height);

    /// <summary>The value row under the header, where the first property is shown and edited in place; null without properties.</summary>
    public Rect? PreviewBounds => Preview is null ? null : new Rect(Node.X, Node.Y + CanvasTheme.HeaderHeight, Width, CanvasTheme.PreviewHeight);

    public Point PinCenter(string pinId) => Pins.First(pin => pin.Pin.Id == pinId).Center;

    public static NodeLayout Create(GraphNode node, NodeDefinition definition, Func<string, double, double> measureText)
    {
        string? preview = PreviewText(node, definition);
        double titleWidth = measureText(definition.DisplayName, CanvasTheme.FontSize) + 28;
        double previewWidth = preview is null ? 0 : measureText(preview, CanvasTheme.SmallFontSize) + 24;

        PinDefinition[] inputs = [.. definition.Pins.Where(pin => pin.Direction == PinDirection.Input)];
        PinDefinition[] outputs = [.. definition.Pins.Where(pin => pin.Direction == PinDirection.Output)];
        double rowWidth = 0;
        for (int row = 0; row < Math.Max(inputs.Length, outputs.Length); row++)
        {
            double left = row < inputs.Length ? measureText(PinLabel(inputs[row]), CanvasTheme.SmallFontSize) : 0;
            double right = row < outputs.Length ? measureText(PinLabel(outputs[row]), CanvasTheme.SmallFontSize) : 0;
            rowWidth = Math.Max(rowWidth, left + right + 56);
        }

        double width = Math.Ceiling(Math.Max(CanvasTheme.MinNodeWidth, Math.Max(titleWidth, Math.Max(previewWidth, rowWidth))) / 8) * 8;
        double top = node.Y + CanvasTheme.HeaderHeight + (preview is null ? 0 : CanvasTheme.PreviewHeight);

        List<PinLayout> pins = [];
        for (int i = 0; i < inputs.Length; i++)
        {
            pins.Add(new PinLayout(inputs[i], new Point(node.X + PinInset, top + (i * CanvasTheme.RowHeight) + (CanvasTheme.RowHeight / 2))));
        }

        for (int i = 0; i < outputs.Length; i++)
        {
            pins.Add(new PinLayout(outputs[i], new Point(node.X + width - PinInset, top + (i * CanvasTheme.RowHeight) + (CanvasTheme.RowHeight / 2))));
        }

        int rows = Math.Max(inputs.Length, outputs.Length);
        double height = (top - node.Y) + (rows * CanvasTheme.RowHeight) + 8;
        return new NodeLayout(node, definition, width, preview, pins, height);
    }

    /// <summary>Pin text: the name, plus the type for data pins so type never depends on color alone.</summary>
    public static string PinLabel(PinDefinition pin) => pin.DataType is null
        ? pin.DisplayName
        : $"{pin.DisplayName}  {pin.DataType.DisplayName}";

    private static string? PreviewText(GraphNode node, NodeDefinition definition)
    {
        if (definition.Properties.Count == 0)
        {
            return null;
        }

        PropertyDefinition property = definition.Properties[0];
        string value = node.Properties.GetValueOrDefault(property.Id, property.DefaultValue);
        if (property.Type == Compiler.TypeSystem.TypeRef.String)
        {
            value = "\"" + value
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
        }

        return value.Length > 40 ? value[..39] + "…" : value;
    }
}
