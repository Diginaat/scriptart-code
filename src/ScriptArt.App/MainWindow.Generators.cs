using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ScriptArt.Compiler.Nodes;
using ScriptArt.ProjectSystem;

namespace ScriptArt.App;

/// <summary>
/// Source generators of the project's packages, such as Godot's (D-050). The IDE runs them while checking code only after the user
/// approves them, since that runs their code in the IDE; opening a project never does.
/// </summary>
public partial class MainWindow
{
    // The generators already offered for approval in this session, so dismissing the offer is not asked again on every reload.
    private readonly HashSet<string> sourceGeneratorsOffered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offers to approve the project's unapproved source generators, once per generator and session unless asked from the menu.</summary>
    private void OfferSourceGenerators(bool always = false)
    {
        if (workspace is null)
        {
            return;
        }

        string[] unapproved = [.. workspace.Project.UnapprovedSourceGenerators()];
        if (unapproved.Length == 0)
        {
            if (always)
            {
                SetStatus(workspace.Project.References.SourceGeneratorPaths.Count == 0
                    ? "The project's packages have no source generators. Restore the packages first if they are not restored."
                    : "The project's source generators are approved and run while the code is checked.");
            }

            return;
        }

        if (!always && unapproved.All(sourceGeneratorsOffered.Contains))
        {
            return;
        }

        sourceGeneratorsOffered.UnionWith(unapproved);
        var approve = new Button { Content = "Run Them", IsDefault = true };
        var later = new Button { Content = "Not Now", IsCancel = true };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Top,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock { Text = "Run the project's source generators?", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Without them, code that uses what they generate (such as Godot's SignalName) shows errors that a build would not. "
                            + "They run inside the IDE, so approve only generators you trust:\n" + string.Join("\n", unapproved),
                        Classes = { "secondary" },
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { approve, later } },
                },
            },
        };
        approve.Click += (_, _) =>
        {
            flyout.Hide();
            ApproveSourceGenerators(unapproved);
        };
        later.Click += (_, _) =>
        {
            flyout.Hide();
            SetStatus("Source generators not run. Project → Approve Source Generators runs them later.");
        };
        Dispatcher.UIThread.Post(() => flyout.ShowAt(ProjectText), DispatcherPriority.Background);
    }

    private void ApproveSourceGenerators(string[] paths)
    {
        if (workspace is null)
        {
            return;
        }

        try
        {
            Compiler.Emit.SourceGeneratorAssembly[] approved = [.. paths.Select(path => new Compiler.Emit.SourceGeneratorAssembly(path, NodeLibraryLoader.HashFile(path)))];
            SavePreferences(preferences.WithApprovedSourceGenerators(approved));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not read the source generator", exception);
            return;
        }

        workspace.Project.ApprovedSourceGenerators = preferences.ApprovedSourceGenerators;
        AppendOutput($"Approved {paths.Length} source generator(s); they run while the code is checked.");
        CheckHandwrittenCode();
        Recompile();
    }

    private void OnApproveSourceGenerators(object? sender, RoutedEventArgs e) => OfferSourceGenerators(always: true);
}
