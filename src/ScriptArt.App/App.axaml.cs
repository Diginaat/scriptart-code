using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ScriptArt.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // "ScriptArtCode path/to/App.vside" opens a project. Opening never builds or runs it.
            if (desktop.Args is [var projectPath, ..] && projectPath.EndsWith(ProjectSystem.VisualProject.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                window.Opened += (_, _) => window.OpenProjectFile(projectPath);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
