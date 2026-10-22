using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.App;

/// <summary>The Variables section of the Details panel, shown when no single node is selected.</summary>
public partial class MainWindow
{
    // The palette lists Get and Set nodes per variable; it is rebuilt only when the variables themselves change.
    private string paletteVariables = "";

    // After adding a variable, its name box takes focus once the panel has been rebuilt.
    private string? focusVariableId;

    private void RefreshPaletteForVariables()
    {
        string signature = workspace is null
            ? ""
            : string.Join("\n", workspace.Editor.Graph.Variables.Select(variable => $"{variable.Id}:{variable.Name}:{variable.Type}"));
        if (signature != paletteVariables)
        {
            paletteVariables = signature;
            PopulatePalette();
        }
    }

    private void AddVariableEditors()
    {
        if (workspace is null)
        {
            return;
        }

        GraphDocument graph = workspace.Editor.Graph;
        InspectorPanel.Children.Add(new TextBlock { Text = "VARIABLES", Classes = { "panelTitle" }, Margin = new Avalonia.Thickness(0, 14, 0, 4) });
        foreach (GraphVariable variable in graph.Variables)
        {
            AddVariableEditor(variable);
        }

        var add = new Button { Content = "Add Variable", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            GraphVariable variable = workspace.Editor.AddVariable("int");
            focusVariableId = variable.Id;
            ShowInspector();
        };
        InspectorPanel.Children.Add(add);
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = graph.Variables.Count == 0
                ? "Variables hold values that change while the program runs."
                : "Each variable has a Get and a Set node in the Variables category of the palette.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "secondary" },
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        });
    }

    private void AddVariableEditor(GraphVariable variable)
    {
        string id = variable.Id;
        var name = new TextBox { Text = variable.Name, MinWidth = 0 };
        var type = new ComboBox
        {
            ItemsSource = TypeRef.VariableTypes.Select(candidate => candidate.Keyword).ToList(),
            SelectedItem = variable.Type,
            Width = 86,
            Margin = new Avalonia.Thickness(4, 0),
        };
        var delete = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 4) };
        ToolTip.SetTip(delete, $"Delete '{variable.Name}' and its Get and Set nodes");
        var value = new TextBox { Text = variable.Value, PlaceholderText = "Initial value", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        var error = new TextBlock { Foreground = (IBrush)this.FindResource("IdeError")!, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var row = new DockPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(delete, Dock.Right);
        DockPanel.SetDock(type, Dock.Right);
        row.Children.Add(delete);
        row.Children.Add(type);
        row.Children.Add(name);

        string? NameProblem(string text) =>
            !GraphCompiler.IsValidIdentifier(text) ? $"'{text}' is not a valid C# name."
            : workspace!.Editor.Graph.Variables.Exists(other => other.Id != id && other.Name == text) ? $"Another variable is named '{text}'."
            : null;
        string? ValueProblem(string text) =>
            TypeRef.FromKeyword(variable.Type) is { } typeRef && !LiteralValues.TryParse(typeRef, text, out _, out string? problem) ? problem : null;
        void Validate() => error.Text = NameProblem(name.Text ?? "") ?? ValueProblem(value.Text ?? "") ?? "";

        // Invalid names and values are still stored, like node values; the compiler reports them until they are fixed.
        void CommitName()
        {
            if (workspace?.Editor.Graph.FindVariable(id) is { } current && (name.Text ?? "") != current.Name)
            {
                workspace.Editor.RenameVariable(id, name.Text ?? "");
            }
        }

        void CommitValue()
        {
            if (workspace?.Editor.Graph.FindVariable(id) is { } current && (value.Text ?? "") != current.Value)
            {
                workspace.Editor.SetVariableValue(id, value.Text ?? "");
            }
        }

        name.TextChanged += (_, _) => Validate();
        value.TextChanged += (_, _) => Validate();
        name.LostFocus += (_, _) => CommitName();
        value.LostFocus += (_, _) => CommitValue();
        name.KeyDown += (_, e) => CommitOnEnter(e, CommitName);
        value.KeyDown += (_, e) => CommitOnEnter(e, CommitValue);
        type.SelectionChanged += (_, _) =>
        {
            if (type.SelectedItem is string keyword && workspace?.Editor.Graph.FindVariable(id) is { } current && keyword != current.Type)
            {
                workspace.Editor.SetVariableType(id, keyword);
            }
        };
        delete.Click += (_, _) =>
        {
            int nodes = workspace!.Editor.NodesUsing(id).Count;
            workspace.Editor.DeleteVariable(id);
            SetStatus(nodes == 0 ? "Variable deleted." : $"Variable deleted with its {nodes} node(s). Undo restores them.");
        };

        Validate();
        InspectorPanel.Children.Add(row);
        InspectorPanel.Children.Add(value);
        InspectorPanel.Children.Add(error);
        if (focusVariableId == id)
        {
            focusVariableId = null;
            name.AttachedToVisualTree += (_, _) =>
            {
                name.Focus();
                name.SelectAll();
            };
        }
    }

    private static void CommitOnEnter(KeyEventArgs e, Action commit)
    {
        if (e.Key == Key.Enter)
        {
            commit();
            e.Handled = true;
        }
    }
}
