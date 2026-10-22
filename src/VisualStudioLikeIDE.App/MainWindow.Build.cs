using Avalonia.Threading;
using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.App;

/// <summary>Generating C#, building and running with the .NET SDK, and the program's input and output.</summary>
public sealed partial class MainWindow
{
    /// <summary>Generates and verifies code on a background thread, then writes it. Returns true when files are up to date.</summary>
    private async Task<bool> GenerateAsync(bool showOutput)
    {
        if (workspace is null || busy)
        {
            return false;
        }

        recompileTimer.Stop();
        Recompile();
        SetBusy(true, "Generating and verifying C#…");
        try
        {
            VisualProject project = workspace.Project;
            GraphCompiler compiler = workspace.Compiler;
            ProjectGenerationResult result = await Task.Run(() => project.Generate(compiler));
            ShowProblems(result.Diagnostics);
            if (!result.Success)
            {
                BottomTabs.SelectedItem = ProblemsTab;
                SetStatus("Generation stopped: fix the problems listed below. No files were written.");
                return false;
            }

            string summary = result.ChangedFiles.Count == 0
                ? "Generated code is up to date."
                : $"Updated {string.Join(", ", result.ChangedFiles.Select(file => Path.GetRelativePath(project.DirectoryPath, file)))}.";
            if (showOutput)
            {
                AppendOutput(summary);
            }

            SetStatus(summary);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not write generated code", exception);
            return false;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task<bool> BuildAsync()
    {
        if (workspace is null || busy || runningProgram is not null || !TrySave())
        {
            return false;
        }

        BottomTabs.SelectedItem = OutputTab;
        OutputEditor.Text = "";
        if (!await GenerateAsync(showOutput: true))
        {
            return false;
        }

        SetBusy(true, "Building with dotnet build…");
        buildCancellation = new CancellationTokenSource();
        try
        {
            AppendOutput($"> dotnet build {workspace.Project.CsprojPath} -c {Configuration}");
            int exitCode = await DotNetCli.BuildAsync(workspace.Project.CsprojPath, Configuration,
                line => Dispatcher.UIThread.Post(() => AppendOutput(line)), buildCancellation.Token);
            bool succeeded = exitCode == 0;
            SetStatus(succeeded ? "Build succeeded." : $"Build failed (exit code {exitCode}). See Output.");

            // The build restores packages, which can make new .NET methods available as nodes.
            if (workspace.LoadNewReferences())
            {
                paletteVariables = "";
                RefreshPaletteForVariables();
                Recompile();
                AppendOutput("Added nodes for the methods of newly referenced assemblies (palette category .NET).");
            }
            return succeeded;
        }
        catch (OperationCanceledException)
        {
            AppendOutput("Build cancelled.");
            SetStatus("Build cancelled.");
            return false;
        }
        catch (InvalidOperationException exception)
        {
            ReportError("Could not start the build", exception);
            return false;
        }
        finally
        {
            buildCancellation.Dispose();
            buildCancellation = null;
            SetBusy(false, null);
        }
    }

    private void SendInput()
    {
        if (runningProgram is null)
        {
            SetStatus("No program is running. Press F5 to run it.");
            return;
        }

        string line = InputBox.Text ?? "";
        AppendOutput(line);
        runningProgram.WriteInput(line);
        InputBox.Text = "";
    }

    private void AppendOutput(string line)
    {
        OutputEditor.Document.Insert(OutputEditor.Document.TextLength, line + "\n");
        OutputEditor.ScrollToLine(OutputEditor.Document.LineCount);
    }
}
