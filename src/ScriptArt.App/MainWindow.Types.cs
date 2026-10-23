using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ScriptArt.Compiler;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>Adding classes and structs, and the Class or Struct section of the Details panel with the type's properties.</summary>
public partial class MainWindow
{
    // After adding a property, its name box takes focus once the panel has been rebuilt.
    private string? focusPropertyId;

    private static string TypeSignatureText(GraphDocument type) =>
        string.Join(":", [type.Id, type.Name, type.Kind.ToString(), string.Join(",", type.TypeParameters), .. type.Properties.Select(property => $"{property.Id}/{property.Name}/{property.Type}")]);

    /// <summary>
    /// The names offered for variable, parameter, return, and property types: the simple types, lists, the project's classes and
    /// structs, and the type parameters of the open class or struct or of the one owning the open method or constructor.
    /// </summary>
    private List<string> TypeNames() =>
        [.. TypeRef.TypesOf(workspace?.Project.GraphDocuments ?? [], workspace?.Editor.Graph).Select(type => type.DisplayName)];

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
            ? $"{workspace!.Graph.Graph.Name} is a {workspace.Graph.Graph.Kind.ToString().ToLowerInvariant()}: it has properties instead of nodes. Edit them in Details, and use its New, Get, and {(workspace.Graph.Graph.Kind == GraphKind.Struct ? "With" : "Set")} nodes from the Types category in the program and function graphs. Add Method and Add Constructor give it methods and constructors."
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
        var nameError = new TextBlock { Foreground = Token("IdeError"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
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

        var typeParameters = new TextBox { Text = string.Join(", ", graph.TypeParameters), PlaceholderText = "none, or for example T" };
        ToolTip.SetTip(typeParameters, $"Comma-separated type parameters, such as T, make {graph.Name} generic; use them as property, parameter, and return types");
        void CommitTypeParameters()
        {
            string[] names = (typeParameters.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (workspace?.Editor.Graph == graph && !names.SequenceEqual(graph.TypeParameters, StringComparer.Ordinal))
            {
                workspace.Editor.SetTypeParameters(names);
            }
        }

        typeParameters.LostFocus += (_, _) => CommitTypeParameters();
        typeParameters.KeyDown += (_, e) => CommitOnEnter(e, CommitTypeParameters);
        InspectorPanel.Children.Add(new TextBlock { Text = "Type parameters", Classes = { "secondary" }, FontSize = 12, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        InspectorPanel.Children.Add(typeParameters);
        if (!isStruct)
        {
            AddBaseTypeEditor(graph);
            AddSignalEditors(graph);
        }

        InspectorPanel.Children.Add(new TextBlock { Text = "Properties", Classes = { "secondary" }, FontSize = 12, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        foreach (GraphProperty property in graph.Properties)
        {
            AddPropertyEditor(graph, property);
        }

        var add = new Button { Content = "Add Property", Margin = new Avalonia.Thickness(0, 4, 6, 0) };
        add.Click += (_, _) =>
        {
            GraphProperty property = workspace.Editor.AddProperty("int");
            focusPropertyId = property.Id;
            ShowInspector();
        };
        var addMethod = new Button { Content = "Add Method", Margin = new Avalonia.Thickness(0, 4, 6, 0) };
        ToolTip.SetTip(addMethod, $"Add a method graph to {graph.Name}; call it with its Call node and use Self inside it");
        addMethod.Click += (_, _) =>
        {
            ProjectGraph method = workspace.AddMethod(workspace.Graph);
            ShowOpenGraph();
            SetStatus($"Added method {method.Graph.Name} to {graph.Name}. Rename it and add parameters in Details; Self is the {graph.Name} it runs on.");
        };
        var addConstructor = new Button { Content = "Add Constructor", Margin = new Avalonia.Thickness(0, 4, 6, 0) };
        ToolTip.SetTip(addConstructor, $"Add a constructor graph to {graph.Name}; choose it on a New node and set properties through Self inside it");
        addConstructor.Click += (_, _) =>
        {
            ProjectGraph constructor = workspace.AddConstructor(workspace.Graph);
            ShowOpenGraph();
            SetStatus($"Added constructor {constructor.Graph.Name} to {graph.Name}. Add parameters in Details; New {graph.Name} nodes offer it.");
        };
        InspectorPanel.Children.Add(new WrapPanel { Children = { add, addMethod, addConstructor } });
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

    // The class a class graph derives from (a Godot node type in a Godot project), and its virtual methods to override (D-047).
    // Godot signals of a class with a base class (D-051): a name and the values it sends, as "int damage, string source".
    private void AddSignalEditors(GraphDocument graph)
    {
        if (graph.BaseType is null && graph.Signals.Count == 0)
        {
            return;
        }

        InspectorPanel.Children.Add(new TextBlock { Text = "Signals", Classes = { "secondary" }, FontSize = 12, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        foreach (GraphSignal signal in graph.Signals)
        {
            string id = signal.Id;
            var name = new TextBox { Text = signal.Name, MinWidth = 0 };
            var values = new TextBox
            {
                Text = NodeEditor.GraphEditor.FormatParameters(signal.Parameters),
                PlaceholderText = "Values it sends, such as int damage",
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            };
            var delete = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 4) };
            ToolTip.SetTip(delete, $"Delete signal '{signal.Name}'");
            ToolTip.SetTip(name, "The signal's name; Emit and On nodes under Signals use it");
            void CommitName()
            {
                if (workspace?.Editor.Graph == graph && graph.Signals.Find(other => other.Id == id) is { } current && (name.Text ?? "") != current.Name)
                {
                    workspace.Editor.RenameSignal(id, name.Text ?? "");
                }
            }

            void CommitValues()
            {
                if (workspace?.Editor.Graph == graph && !workspace.Editor.SetSignalParameters(id, values.Text ?? ""))
                {
                    SetStatus("Write the values as a type and a name each, separated by commas, such as: int damage, string source");
                }
            }

            name.LostFocus += (_, _) => CommitName();
            name.KeyDown += (_, e) => CommitOnEnter(e, CommitName);
            values.LostFocus += (_, _) => CommitValues();
            values.KeyDown += (_, e) => CommitOnEnter(e, CommitValues);
            delete.Click += (_, _) =>
            {
                workspace!.Editor.DeleteSignal(id);
                SetStatus("Signal deleted. Its Emit and On nodes report an error until they are deleted; undo restores it.");
            };
            var row = new DockPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(name);
            InspectorPanel.Children.Add(row);
            InspectorPanel.Children.Add(values);
        }

        var add = new Button { Content = "+ Add Signal", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(add, $"Declare a Godot signal of {graph.Name}; emit it with an Emit node and handle it with an On node (Signals category)");
        add.Click += (_, _) =>
        {
            workspace!.Editor.AddSignal();
            ShowInspector();
        };
        InspectorPanel.Children.Add(add);
    }

    private void AddBaseTypeEditor(GraphDocument graph)
    {
        NodeLibrary library = workspace!.Compiler.Library;
        var baseType = new AutoCompleteBox
        {
            Text = graph.BaseType ?? "",
            ItemsSource = library.ExternalTypes.Where(type => type.IsInheritable).Select(type => type.Type.SourceName).ToArray(),
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 1,
            PlaceholderText = workspace.Project.IsGodotProject ? "none, or for example Godot.Node2D" : "none",
        };
        ToolTip.SetTip(baseType, $"The .NET class {graph.Name} derives from. In a Godot project, a node type makes {graph.Name} a script you can attach in Godot.");
        void CommitBaseType()
        {
            if (workspace?.Editor.Graph == graph && (string.IsNullOrWhiteSpace(baseType.Text) ? null : baseType.Text.Trim()) != graph.BaseType)
            {
                workspace.Editor.SetBaseType(baseType.Text);
                ShowInspector();
            }
        }

        baseType.LostFocus += (_, _) => CommitBaseType();
        baseType.KeyDown += (_, e) => CommitOnEnter(e, CommitBaseType);
        InspectorPanel.Children.Add(new TextBlock { Text = "Base class", Classes = { "secondary" }, FontSize = 12, Margin = new Avalonia.Thickness(0, 4, 0, 0) });
        InspectorPanel.Children.Add(baseType);

        IReadOnlyList<GraphDocument> graphs = workspace.Project.GraphDocuments;
        ExternalMethod[] overridable = [.. library.OverridableMethods(graph, graphs)
            .Where(method => !graphs.Any(other => other.Owner == graph.Id && other.Kind == GraphKind.Function && other.Name == method.Name))];
        if (overridable.Length == 0)
        {
            return;
        }

        var overrideButton = new Button { Content = "Override Method", Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(overrideButton, $"Add a method that replaces a virtual method of {graph.BaseType}, such as _Ready or _Process; Godot calls it");
        overrideButton.Click += (_, _) =>
        {
            var flyout = new MenuFlyout();
            foreach (ExternalMethod method in overridable)
            {
                var item = new MenuItem { Header = $"{method.Name}({string.Join(", ", method.Parameters.Select(parameter => $"{parameter.Type.DisplayName} {parameter.Name}"))})" };
                item.Click += (_, _) =>
                {
                    ProjectGraph added = workspace.AddOverride(workspace.Graph, method);
                    ShowOpenGraph();
                    SetStatus($"Added {added.Graph.Name} to {graph.Name}. It overrides {method.DisplayName}.");
                };
                flyout.Items.Add(item);
            }

            flyout.ShowAt(overrideButton);
        };
        InspectorPanel.Children.Add(overrideButton);
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
        var error = new TextBlock { Foreground = Token("IdeError"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };

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
        if (graph.BaseType is not null || property.Export)
        {
            var export = new CheckBox { Content = "Export to Godot", IsChecked = property.Export };
            ToolTip.SetTip(export, $"[Export]: the Godot editor shows {property.Name} on each node with this script and saves its value in the scene");
            export.IsCheckedChanged += (_, _) =>
            {
                if (workspace?.Editor.Graph == graph && graph.FindProperty(id) is { } current && export.IsChecked != current.Export)
                {
                    workspace.Editor.SetPropertyExport(id, export.IsChecked == true);
                }
            };
            InspectorPanel.Children.Add(export);
        }

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
