using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.Compiler.TypeSystem;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.App;

/// <summary>Adding classes and structs, and the Class or Struct section of the Details panel with the type's properties.</summary>
public partial class MainWindow
{
    // After adding a property, its name box takes focus once the panel has been rebuilt.
    private string? focusPropertyId;

    private static string TypeSignatureText(GraphDocument type) =>
        string.Join(":", [type.Id, type.Name, type.Kind.ToString(), .. type.Properties.Select(property => $"{property.Id}/{property.Name}/{property.Type}")]);

    /// <summary>The names offered for variable, parameter, return, and property types: the simple types, lists, and the project's classes and structs.</summary>
    private List<string> TypeNames() =>
        [.. TypeRef.TypesOf(workspace?.Project.GraphDocuments ?? []).Select(type => type.DisplayName)];

    private void OnAddClass(object? sender, RoutedEventArgs e) => AddType(GraphKind.Class);

    private void OnAddStruct(object? sender, RoutedEventArgs e) => AddType(GraphKind.Struct);

    private void AddType(GraphKind kind)
    {
        if (workspace is null)
        {
            return;
        }

        ProjectGraph added = workspace.AddType(kind);
        ShowOpenGraph();
        SetStatus(kind == GraphKind.Class
            ? $"Added class {added.Graph.Name}. Rename it and add properties in Details; create objects of it with its New node."
            : $"Added struct {added.Graph.Name}. Rename it and add properties in Details; create values of it with its New node.");
    }

    private void ShowTypeGraphHint()
    {
        bool isType = workspace is not null && TypeRef.IsTypeGraph(workspace.Graph.Graph);
        TypeGraphHint.IsVisible = isType;
        TypeGraphHint.Text = isType
            ? $"{workspace!.Graph.Graph.Name} is a {workspace.Graph.Graph.Kind.ToString().ToLowerInvariant()}: it has properties instead of nodes. Edit them in Details, and use its New, Get, and {(workspace.Graph.Graph.Kind == GraphKind.Struct ? "With" : "Set")} nodes from the Types category in the program and function graphs."
            : "";
    }

    private void AddTypeEditors()
    {
        if (workspace is null || !TypeRef.IsTypeGraph(workspace.Editor.Graph))
        {
            return;
        }

        GraphDocument graph = workspace.Editor.Graph;
        bool isStruct = graph.Kind == GraphKind.Struct;
        InspectorPanel.Children.Add(new TextBlock { Text = isStruct ? "STRUCT" : "CLASS", Classes = { "panelTitle" }, Margin = new Avalonia.Thickness(0, 14, 0, 4) });

        var name = new TextBox { Text = graph.Name };
        var nameError = new TextBlock { Foreground = (IBrush)this.FindResource("IdeError")!, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        void ValidateName() => nameError.Text = GraphCompiler.IsValidIdentifier(name.Text ?? "") ? "" : $"'{name.Text}' is not a valid C# type name.";
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

        InspectorPanel.Children.Add(new TextBlock { Text = "Properties", Classes = { "secondary" }, FontSize = 12, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        foreach (GraphProperty property in graph.Properties)
        {
            AddPropertyEditor(graph, property);
        }

        var add = new Button { Content = "Add Property", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            GraphProperty property = workspace.Editor.AddProperty("int");
            focusPropertyId = property.Id;
            ShowInspector();
        };
        InspectorPanel.Children.Add(add);
        InspectorPanel.Children.Add(new TextBlock
        {
            Text = isStruct
                ? "A struct is a value: assigning it copies it. With nodes make a copy with one property changed; store the copy with a Set node."
                : "A class object is shared: every variable that holds it sees changes made with its Set nodes. Renaming the class does not update variables that use it.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "secondary" },
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        });
    }

    private void AddPropertyEditor(GraphDocument graph, GraphProperty property)
    {
        string id = property.Id;
        var name = new TextBox { Text = property.Name, MinWidth = 0 };
        var type = new ComboBox
        {
            ItemsSource = TypeNames(),
            SelectedItem = property.Type,
            Width = 116,
            Margin = new Avalonia.Thickness(4, 0),
        };
        var delete = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 4) };
        ToolTip.SetTip(delete, $"Delete property '{property.Name}'");
        var value = new TextBox { Text = property.Value, PlaceholderText = "Initial value", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        var error = new TextBlock { Foreground = (IBrush)this.FindResource("IdeError")!, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var row = new DockPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(delete, Dock.Right);
        DockPanel.SetDock(type, Dock.Right);
        row.Children.Add(delete);
        row.Children.Add(type);
        row.Children.Add(name);

        string? NameProblem(string text) =>
            !GraphCompiler.IsValidIdentifier(text) ? $"'{text}' is not a valid C# name."
            : graph.Properties.Exists(other => other.Id != id && other.Name == text) ? $"Another property is named '{text}'."
            : text == graph.Name ? $"A property cannot have the name of its {graph.Kind.ToString().ToLowerInvariant()}."
            : null;
        string? ValueProblem(string text) =>
            graph.FindProperty(id) is { } current && TypeRef.FromName(current.Type, workspace!.Project.GraphDocuments) is { } typeRef
            && LiteralValues.TryInitialValue(typeRef, text, out string? problem) is null ? problem : null;
        void Validate() => error.Text = NameProblem(name.Text ?? "") ?? ValueProblem(value.Text ?? "") ?? "";

        // Like variables, invalid names and values are still stored; the compiler reports them until they are fixed.
        void CommitName()
        {
            if (workspace?.Editor.Graph == graph && graph.FindProperty(id) is { } current && (name.Text ?? "") != current.Name)
            {
                workspace.Editor.RenameProperty(id, name.Text ?? "");
            }
        }

        void CommitValue()
        {
            if (workspace?.Editor.Graph == graph && graph.FindProperty(id) is { } current && (value.Text ?? "") != current.Value)
            {
                workspace.Editor.SetPropertyValue(id, value.Text ?? "");
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
            if (type.SelectedItem is string typeName && workspace?.Editor.Graph == graph && graph.FindProperty(id) is { } current && typeName != current.Type)
            {
                workspace.Editor.SetPropertyType(id, typeName);
            }
        };
        delete.Click += (_, _) =>
        {
            workspace!.Editor.DeleteProperty(id);
            SetStatus("Property deleted. Nodes that use it report an error until they are deleted; undo restores it.");
        };

        Validate();
        InspectorPanel.Children.Add(row);
        InspectorPanel.Children.Add(value);
        InspectorPanel.Children.Add(error);
        if (focusPropertyId == id)
        {
            focusPropertyId = null;
            name.AttachedToVisualTree += (_, _) =>
            {
                name.Focus();
                name.SelectAll();
            };
        }
    }
}
