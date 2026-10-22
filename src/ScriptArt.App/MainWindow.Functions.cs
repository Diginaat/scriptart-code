using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ScriptArt.Compiler;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>Adding function graphs, and the Function section of the Details panel, also used for methods and constructors.</summary>
public partial class MainWindow
{
    private const string NoReturnValue = "(none)";

    // After adding a parameter, its name box takes focus once the panel has been rebuilt.
    private string? focusParameterId;

    private static string FunctionSignatureText(GraphDocument function) =>
        string.Join(":", [function.Id, function.Name, function.ReturnType ?? "", .. function.Parameters.Select(parameter => $"{parameter.Id}/{parameter.Name}/{parameter.Type}")]);

    private void OnAddFunction(object? sender, RoutedEventArgs e)
    {
        if (workspace is null)
        {
            return;
        }

        ProjectGraph added = workspace.AddFunction();
        ShowOpenGraph();
        SetStatus($"Added function {added.Graph.Name}. Rename it and add parameters in Details; call it from any graph with its Call node.");
    }

    private void AddFunctionEditors()
    {
        if (workspace is null || workspace.Editor.Graph.Kind is not (GraphKind.Function or GraphKind.Constructor))
        {
            return;
        }

        GraphDocument graph = workspace.Editor.Graph;
        string? owner = workspace.Project.GraphDocuments.FirstOrDefault(type => type.Id == workspace.Editor.Graph.Owner)?.Name;
        bool isConstructor = graph.Kind == GraphKind.Constructor;
        string title = owner is null ? "FUNCTION" : $"{(isConstructor ? "CONSTRUCTOR" : "METHOD")} OF {owner.ToUpperInvariant()}";
        InspectorPanel.Children.Add(new TextBlock { Text = title, Classes = { "panelTitle" }, Margin = new Avalonia.Thickness(0, 14, 0, 4) });

        var name = new TextBox { Text = graph.Name };
        var nameError = new TextBlock { Foreground = (IBrush)this.FindResource("IdeError")!, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        void ValidateName() => nameError.Text = GraphCompiler.IsValidIdentifier(name.Text ?? "") ? "" : $"'{name.Text}' is not a valid C# method name.";
        void CommitName()
        {
            if (workspace?.Editor.Graph == graph && (name.Text ?? "") != graph.Name)
            {
                workspace.Editor.RenameGraph(name.Text ?? "");
            }
        }

        name.TextChanged += (_, _) => ValidateName();
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => CommitOnEnter(e, CommitName);
        ValidateName();
        InspectorPanel.Children.Add(new TextBlock { Text = "Name", Classes = { "secondary" }, FontSize = 12 });
        InspectorPanel.Children.Add(name);
        InspectorPanel.Children.Add(nameError);

        var returnType = new ComboBox
        {
            ItemsSource = new[] { NoReturnValue }.Concat(TypeNames()).ToList(),
            SelectedItem = graph.ReturnType ?? NoReturnValue,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        returnType.SelectionChanged += (_, _) =>
        {
            string? type = returnType.SelectedItem as string == NoReturnValue ? null : returnType.SelectedItem as string;
            if (workspace?.Editor.Graph == graph && type != graph.ReturnType)
            {
                workspace.Editor.SetReturnType(type);
            }
        };
        if (!isConstructor)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = "Returns", Classes = { "secondary" }, FontSize = 12, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
            InspectorPanel.Children.Add(returnType);
        }

        InspectorPanel.Children.Add(new TextBlock { Text = "Parameters", Classes = { "secondary" }, FontSize = 12, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        foreach (GraphParameter parameter in graph.Parameters)
        {
            AddParameterEditor(graph, parameter);
        }

        var add = new Button { Content = "Add Parameter", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            GraphParameter parameter = workspace.Editor.AddParameter("int");
            focusParameterId = parameter.Id;
            ShowInspector();
        };
        InspectorPanel.Children.Add(add);
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = isConstructor
                ? $"Parameters are outputs of the Function Entry node and inputs of New {owner} when it uses this constructor. Set properties through Self."
                : graph.ReturnType is null
                ? "Parameters are outputs of the Function Entry node. Call the function from any graph with its Call node."
                : "Parameters are outputs of the Function Entry node. Every path must end at a Return Value node.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "secondary" },
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        });
    }

    private void AddParameterEditor(GraphDocument graph, GraphParameter parameter)
    {
        string id = parameter.Id;
        var name = new TextBox { Text = parameter.Name, MinWidth = 0 };
        var type = new ComboBox
        {
            ItemsSource = TypeNames(),
            SelectedItem = parameter.Type,
            Width = 116,
            Margin = new Avalonia.Thickness(4, 0),
        };
        var delete = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 4) };
        ToolTip.SetTip(delete, $"Delete parameter '{parameter.Name}' and its wires");
        var error = new TextBlock { Foreground = (IBrush)this.FindResource("IdeError")!, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var row = new DockPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(delete, Dock.Right);
        DockPanel.SetDock(type, Dock.Right);
        row.Children.Add(delete);
        row.Children.Add(type);
        row.Children.Add(name);

        void Validate()
        {
            string text = name.Text ?? "";
            error.Text = !GraphCompiler.IsValidIdentifier(text) ? $"'{text}' is not a valid C# name."
                : graph.Parameters.Exists(other => other.Id != id && other.Name == text) ? $"Another parameter is named '{text}'."
                : graph.Variables.Exists(variable => variable.Name == text) ? $"A variable is named '{text}'."
                : "";
        }

        // Like variables, an invalid name is still stored; the compiler reports it until it is fixed.
        void CommitName()
        {
            if (workspace?.Editor.Graph == graph && graph.FindParameter(id) is { } current && (name.Text ?? "") != current.Name)
            {
                workspace.Editor.RenameParameter(id, name.Text ?? "");
            }
        }

        name.TextChanged += (_, _) => Validate();
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => CommitOnEnter(e, CommitName);
        type.SelectionChanged += (_, _) =>
        {
            if (type.SelectedItem is string keyword && workspace?.Editor.Graph == graph && graph.FindParameter(id) is { } current && keyword != current.Type)
            {
                workspace.Editor.SetParameterType(id, keyword);
            }
        };
        delete.Click += (_, _) =>
        {
            workspace!.Editor.DeleteParameter(id);
            SetStatus("Parameter deleted with its wires. Undo restores them.");
        };

        Validate();
        InspectorPanel.Children.Add(row);
        InspectorPanel.Children.Add(error);
        if (focusParameterId == id)
        {
            focusParameterId = null;
            name.AttachedToVisualTree += (_, _) =>
            {
                name.Focus();
                name.SelectAll();
            };
        }
    }
}
