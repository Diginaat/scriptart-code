using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;

namespace ScriptArt.UiTests;

/// <summary>
/// Renders the IDE in both themes to PNG files for a visual check. Runs only when <c>SCRIPTART_SCREENSHOTS</c> names a folder,
/// because the result needs a person (or a reviewer) to look at it.
/// </summary>
public sealed class ScreenshotTests
{
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void RenderTheIde(string theme)
    {
        string? folder = Environment.GetEnvironmentVariable("SCRIPTART_SCREENSHOTS");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "Set SCRIPTART_SCREENSHOTS to a folder to render screenshots.");
        Directory.CreateDirectory(folder!);
        using var session = new IdeSession();
        try
        {
            Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            session.Window.WindowState = Avalonia.Controls.WindowState.Normal;
            session.Window.Width = 1400;
            session.Window.Height = 860;
            session.OpenSample("Types");
            IdeSession.Settle();
            Save(session, folder!, $"{theme}-graph.png");

            session.Press(Key.P, RawInputModifiers.Control);
            session.Type("csproj");
            session.Press(Key.Enter);
            Save(session, folder!, $"{theme}-file.png");

            session.Press(Key.OemComma, RawInputModifiers.Control);
            Save(session, folder!, $"{theme}-preferences.png");
            session.Window.PreferencesPanel.IsVisible = false;
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            IdeSession.Settle();
        }
    }

    private static void Save(IdeSession session, string folder, string name)
    {
        IdeSession.Settle();
        using var frame = session.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
