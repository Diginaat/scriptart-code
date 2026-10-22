using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VisualStudioLikeIDE.App.Canvas;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.NodeEditor;

namespace VisualStudioLikeIDE.App;

/// <summary>Editing node values: the details panel and the inline editor over a node's value row.</summary>
public sealed partial class MainWindow
{
    private InlineEdit? inlineEdit;

    private void BeginValueEdit(ValueEditRequestedEventArgs request)
    {
        if (workspace?.Editor.Graph.FindNode(request.NodeId) is not { } node || workspace.Editor.DefinitionOf(node) is not { } definition)
        {
            return;
        }

        EndInlineEdit(commit: true);
        GraphEditor editor = workspace.Editor;
        PropertyDefinition property = request.Property;
        string current = node.Properties.GetValueOrDefault(property.Id, property.DefaultValue);
        if (property.Type == Compiler.TypeSystem.TypeRef.Boolean)
        {
            // A two-state value needs no text editor.
            editor.SetProperty(node.Id, property.Id, current == "true" ? "false" : "true");
            return;
        }

        if (property.Choices is not null)
        {
            // A fixed set of values is picked from the list in Details.
            GraphCanvas.Select(node.Id, frame: false);
            SetStatus($"Choose {property.DisplayName} from the list in Details.");
            return;
        }

        // Invalid text is stored too; the compiler reports it on the node so it can be fixed in place.
        ShowInlineEditor(request.Screen, multiline: property.Type == Compiler.TypeSystem.TypeRef.String, new InlineEdit(
            current,
            text => definition.ValidateProperty(property, text),
            text =>
            {
                if (editor.Graph.FindNode(node.Id) is not null)
                {
                    editor.SetProperty(node.Id, property.Id, text);
                }
            }));
    }

    private void BeginCommentEdit(CommentEditRequestedEventArgs request)
    {
        if (workspace?.Editor.Graph.FindComment(request.CommentId) is not { } comment)
        {
            return;
        }

        EndInlineEdit(commit: true);
        GraphEditor editor = workspace.Editor;
        ShowInlineEditor(request.Screen, multiline: false, new InlineEdit(
            comment.Text,
            _ => null,
            text =>
            {
                if (editor.Graph.FindComment(comment.Id) is not null)
                {
                    editor.SetCommentText(comment.Id, text);
                }
            }));
    }

    private void ShowInlineEditor(Avalonia.Rect screen, bool multiline, InlineEdit edit)
    {
        inlineEdit = edit;
        InlineEditor.AcceptsReturn = multiline;
        InlineEditor.Text = edit.Original;
        InlineEditor.Margin = new Avalonia.Thickness(screen.X, screen.Y, 0, 0);
        InlineEditor.Width = Math.Max(screen.Width, multiline ? 300 : 160);
        InlineEditor.MinHeight = multiline ? Math.Max(screen.Height, 64) : Math.Max(screen.Height, 24);
        ToolTip.SetTip(InlineEditor, multiline ? "Enter applies, Shift+Enter inserts a line break, Esc cancels." : "Enter applies, Esc cancels.");
        ValidateInlineEdit();
        InlineEditor.IsVisible = true;
        InlineEditor.Focus();
        InlineEditor.SelectAll();
    }

    private void ValidateInlineEdit()
    {
        if (inlineEdit is null)
        {
            return;
        }

        string? error = inlineEdit.Validate(InlineEditor.Text ?? "");
        InlineEditor.BorderBrush = error is null ? null : (IBrush)this.FindResource("IdeError")!;
        SetStatus(error ?? (InlineEditor.AcceptsReturn ? $"{TextSummary(InlineEditor.Text ?? "")} Enter applies, Shift+Enter inserts a line break, Esc cancels." : ""));
    }

    private void EndInlineEdit(bool commit)
    {
        if (inlineEdit is not { } edit)
        {
            return;
        }

        // Cleared first: hiding the box moves focus, which raises LostFocus and would end the edit again.
        inlineEdit = null;
        InlineEditor.IsVisible = false;
        string text = InlineEditor.Text ?? "";
        if (commit && text != edit.Original)
        {
            edit.Apply(text);
        }
    }

    private void ShowInspector()
    {
        InspectorPanel.Children.Clear();
        if (workspace is null)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = "No project open.", Classes = { "secondary" } });
            return;
        }

        if (GraphCanvas.SelectedItems.Count == 1 && workspace.Editor.Graph.FindComment(GraphCanvas.SelectedItems.First()) is { } comment)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = "Comment", FontSize = 16, FontWeight = FontWeight.SemiBold });
            InspectorPanel.Children.Add(new TextBlock { Text = comment.Text, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 8) });
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "Comments only document the graph; they never change the generated code.\n\nDrag the title to move the comment together with the nodes inside it. Double-click the title or press F2 to rename it. Drag the lower-right corner to resize it.",
                TextWrapping = TextWrapping.Wrap,
                Classes = { "secondary" },
            });
            return;
        }

        if (GraphCanvas.SelectedItems.Count != 1 || workspace.Editor.Graph.FindNode(GraphCanvas.SelectedItems.First()) is not { } node)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = workspace.Graph.Graph.Name, FontSize = 16, FontWeight = FontWeight.SemiBold });
            if (Compiler.TypeSystem.TypeRef.IsTypeGraph(workspace.Graph.Graph))
            {
                InspectorPanel.Children.Add(new TextBlock { Text = workspace.Graph.RelativePath, Classes = { "secondary" } });
                AddTypeEditors();
                return;
            }

            InspectorPanel.Children.Add(new TextBlock { Text = $"{workspace.Graph.RelativePath}\n{workspace.Graph.Graph.Nodes.Count} nodes, {workspace.Graph.Graph.Connections.Count} connections", Classes = { "secondary" } });
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = GraphCanvas.SelectedItems.Count > 1
                    ? $"{GraphCanvas.SelectedItems.Count} items selected."
                    : "Select a node to see its details and edit its values.\n\nRight-click the graph to search nodes. Drag from a pin to connect, or release it on empty space to search nodes that fit. Right-drag to pan, scroll to zoom, Alt+click a pin to disconnect. Press C to add a comment around the selection.",
                TextWrapping = TextWrapping.Wrap,
                Classes = { "secondary" },
                Margin = new Avalonia.Thickness(0, 8, 0, 0),
            });
            AddFunctionEditors();
            AddVariableEditors();
            return;
        }

        // An unresolved node shows its stand-in, so its problems are listed here too.
        NodeDefinition definition = workspace.Editor.DefinitionOf(node) ?? workspace.Editor.PlaceholderFor(node);
        InspectorPanel.Children.Add(new TextBlock { Text = definition.DisplayName, FontSize = 16, FontWeight = FontWeight.SemiBold });
        InspectorPanel.Children.Add(new TextBlock { Text = definition.Category, Classes = { "secondary" } });
        InspectorPanel.Children.Add(new TextBlock { Text = definition.Description, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 8) });

        foreach (PropertyDefinition property in definition.Properties)
        {
            AddPropertyEditor(node, definition, property);
        }

        InspectorPanel.Children.Add(new TextBlock { Text = "PINS", Classes = { "panelTitle" }, Margin = new Avalonia.Thickness(0, 10, 0, 2) });
        foreach (PinDefinition pin in definition.Pins)
        {
            string name = pin.DisplayName.Length == 0 ? "Execution" : pin.DisplayName;
            string type = pin.DataType?.DisplayName ?? "execution";
            string direction = pin.Direction == PinDirection.Input ? "in" : "out";
            InspectorPanel.Children.Add(new TextBlock { Text = $"{direction}  {name}: {type}", Classes = { "secondary" }, FontSize = 12 });
        }

        if (workspace.LastCompilation.Diagnostics.Where(diagnostic => diagnostic.NodeId == node.Id).ToList() is { Count: > 0 } problems)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = "PROBLEMS", Classes = { "panelTitle" }, Margin = new Avalonia.Thickness(0, 10, 0, 2) });
            foreach (GraphDiagnostic problem in problems)
            {
                InspectorPanel.Children.Add(new TextBlock
                {
                    Text = problem.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (IBrush)this.FindResource(problem.IsError ? "IdeError" : "IdeWarning")!,
                });
            }
        }
    }

    private void AddPropertyEditor(GraphNode node, NodeDefinition definition, PropertyDefinition property)
    {
        string current = node.Properties.GetValueOrDefault(property.Id, property.DefaultValue);
        if (property.Choices is { } choices)
        {
            // A fixed set of values is picked from a list, so it cannot be mistyped.
            InspectorPanel.Children.Add(new TextBlock { Text = property.DisplayName });
            var list = new ComboBox { ItemsSource = choices, SelectedItem = current, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is string chosen && workspace?.Editor.Graph.FindNode(node.Id) is not null && chosen != node.Properties.GetValueOrDefault(property.Id, property.DefaultValue))
                {
                    workspace.Editor.SetProperty(node.Id, property.Id, chosen);
                }
            };
            InspectorPanel.Children.Add(list);
            return;
        }

        bool isText = property.Type == Compiler.TypeSystem.TypeRef.String;
        InspectorPanel.Children.Add(new TextBlock { Text = isText ? $"{property.DisplayName} (text)" : $"{property.DisplayName} ({property.Type.DisplayName})" });
        var box = new TextBox
        {
            Text = current,
            AcceptsReturn = isText,
            NewLine = "\n",
            TextWrapping = TextWrapping.Wrap,
            MinHeight = isText ? 64 : 0,
            PlaceholderText = isText ? "Type the text. Spaces at the start and end count." : null,
        };
        var error = new TextBlock { Foreground = (IBrush)this.FindResource("IdeError")!, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        void Validate() => error.Text = definition.ValidateProperty(property, box.Text ?? "") ?? "";
        void Commit()
        {
            if (workspace is not null && workspace.Editor.Graph.FindNode(node.Id) is not null && (box.Text ?? "") != node.Properties.GetValueOrDefault(property.Id, property.DefaultValue))
            {
                workspace.Editor.SetProperty(node.Id, property.Id, box.Text ?? "");
            }
        }

        box.TextChanged += (_, _) => Validate();
        box.LostFocus += (_, _) => Commit();
        // Tunnel: a multi-line TextBox consumes Enter itself before bubbling handlers see it.
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                Commit();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Validate();
        InspectorPanel.Children.Add(box);
        InspectorPanel.Children.Add(error);
        if (isText)
        {
            var hint = new TextBlock { Classes = { "secondary" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            void Count() => hint.Text = $"{TextSummary(box.Text ?? "")} Enter applies; Shift+Enter inserts a line break.";
            box.TextChanged += (_, _) => Count();
            Count();
            InspectorPanel.Children.Add(hint);
        }
    }

    // "12 characters, 2 lines." for text values, so spaces and line breaks that are hard to see are still noticed.
    private static string TextSummary(string text)
    {
        int lines = text.Count(character => character == '\n') + 1;
        return $"{text.Length} character{(text.Length == 1 ? "" : "s")}{(lines > 1 ? $", {lines} lines" : "")}.";
    }

    /// <summary>What the inline editor is editing: its starting text, how to check new text, and how to apply it.</summary>
    private sealed record InlineEdit(string Original, Func<string, string?> Validate, Action<string> Apply);
}
