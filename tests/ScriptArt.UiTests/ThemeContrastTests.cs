using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using ScriptArt.App;
using ScriptArt.App.Canvas;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.ProjectSystem;

namespace ScriptArt.UiTests;

/// <summary>
/// The contrast review of both themes (Phase 6, item 4; D-046): text against its background meets WCAG AA (4.5:1) and icons, pins,
/// and outlines meet 3:1. The pairs are those the IDE actually draws.
/// </summary>
public sealed class ThemeContrastTests
{
    public static TheoryData<string> Themes => ["Dark", "Light"];

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void ThemeTokensMeetWcagAA(string themeName)
    {
        ThemeVariant theme = themeName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        Color Token(string key) =>
            Application.Current!.TryGetResource(key, theme, out object? value) && value is ISolidColorBrush brush
                ? brush.Color
                : throw new InvalidOperationException($"Token {key} is missing from the {themeName} theme.");

        List<string> failures = [];
        void Check(string text, string background, double needed)
        {
            double ratio = Contrast(Token(text), Token(background));
            if (ratio < needed)
            {
                failures.Add($"{text} on {background}: {ratio:F2} < {needed}");
            }
        }

        foreach (string background in new[] { "IdeWindowBackground", "IdePanelBackground", "IdePanelHeaderBackground", "IdeHover", "IdeCodeBackground" })
        {
            Check("IdeText", background, 4.5);
            Check("IdeTextSecondary", background, 4.5);
        }

        foreach (string background in new[] { "IdeWindowBackground", "IdePanelBackground" })
        {
            Check("IdeError", background, 4.5);
            Check("IdeWarning", background, 4.5);
            Check("IdeSuccess", background, 4.5);
            Check("IdeAccent", background, 3);
        }

        Check("IdeText", "IdeSelection", 4.5);
        Assert.Empty(failures);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void CanvasAndCodeColorsMeetWcagAA(string themeName)
    {
        bool light = themeName == "Light";
        CanvasTheme.Use(light);
        try
        {
            Color body = ColorOf(CanvasTheme.NodeBody);
            Color background = ColorOf(CanvasTheme.Background);
            List<string> failures = [];
            void Check(string name, Color foreground, Color under, double needed)
            {
                double ratio = Contrast(foreground, under);
                if (ratio < needed)
                {
                    failures.Add($"{name}: {ratio:F2} < {needed}");
                }
            }

            Check("pin label", ColorOf(CanvasTheme.Label), body, 4.5);
            Check("pin type", ColorOf(CanvasTheme.TypeLabel), body, 4.5);
            Check("value preview", ColorOf(CanvasTheme.ValuePreview), body, 4.5);
            Check("empty-graph hint", ColorOf(CanvasTheme.TypeLabel), background, 4.5);
            Check("comment title", ColorOf(CanvasTheme.CommentTitle), Over(ColorOf(CanvasTheme.CommentHeader), background), 4.5);
            Check("frame statistics", ColorOf(CanvasTheme.OverlayText), Over(ColorOf(CanvasTheme.MinimapBackground), background), 4.5);
            foreach (string category in new[] { "Flow", "Console", "Literals", "Math", "Comparison", "Logic", "String", "Conversion", "Variables", "Other" })
            {
                Check($"title on {category}", ColorOf(CanvasTheme.Title), ColorOf(CanvasTheme.ForCategory(category)), 4.5);
            }

            foreach (TypeRef? type in new TypeRef?[] { null, TypeRef.Boolean, TypeRef.Int32, TypeRef.Double, TypeRef.String })
            {
                Check($"pin {type?.DisplayName ?? "execution"}", ColorOf(CanvasTheme.ForType(type)), body, 3);
                Check($"wire {type?.DisplayName ?? "execution"}", ColorOf(CanvasTheme.ForType(type)), background, 3);
            }

            Check("selection outline", ColorOf(CanvasTheme.NodeSelected.Brush!), background, 3);
            Check("error outline", ColorOf(CanvasTheme.NodeError.Brush!), background, 3);
            Check("execution outline", ColorOf(CanvasTheme.ExecutionOutline.Brush!), background, 3);

            Color code = light ? Colors.White : Color.Parse("#1A1B1E");
            foreach (string name in new[] { "Comment", "String", "AttributeName", "NumberLiteral", "MethodCall", "Preprocessor", "Heading", "Keywords" })
            {
                Check($"syntax {name}", Color.Parse(IdeColors.SyntaxColor(name, light)), code, 4.5);
            }

            var diff = IdeColors.Diff(light);
            Check("diff added", ColorOf(diff.Added), ColorOf(diff.AddedBackground), 4.5);
            Check("diff removed", ColorOf(diff.Removed), ColorOf(diff.RemovedBackground), 4.5);
            Check("diff header", ColorOf(diff.Header), code, 4.5);
            Assert.Empty(failures);
        }
        finally
        {
            CanvasTheme.Use(light: false);
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void IconColorsReadOnPanels(string themeName)
    {
        ThemeVariant theme = themeName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        IBrush[] icons = [IdeColors.IconGreen, IdeColors.IconGold, IdeColors.IconPurple, IdeColors.IconOrchid, IdeColors.IconBlue, IdeColors.IconSky, IdeColors.IconKhaki, IdeColors.IconGray];
        foreach (string key in new[] { "IdePanelBackground", "IdePanelHeaderBackground", "IdeWindowBackground" })
        {
            Assert.True(Application.Current!.TryGetResource(key, theme, out object? value));
            Color panel = ((ISolidColorBrush)value!).Color;
            Assert.All(icons, icon => Assert.True(Contrast(ColorOf(icon), panel) >= 3, $"{ColorOf(icon)} on {key}: {Contrast(ColorOf(icon), panel):F2}"));
        }
    }

    [AvaloniaFact]
    public void ChoosingTheLightThemeRecolorsTheIde()
    {
        using var session = new IdeSession();
        session.OpenSample("HelloWorld");
        try
        {
            session.Press(Key.OemComma, RawInputModifiers.Control);
            session.Window.ThemeBox.SelectedIndex = (int)IdeTheme.Light;
            IdeSession.Click(session.Window.PreferencesPanel.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == "Save"));

            Assert.Equal(ThemeVariant.Light, session.Window.ActualThemeVariant);
            Assert.True(CanvasTheme.IsLight);
            Assert.Equal(Colors.White, ((ISolidColorBrush)session.Window.FindResource(session.Window.ActualThemeVariant, "IdeWindowBackground")!).Color);
            Assert.Equal(IdeTheme.Light, PreferencesStore.Load(session.PreferencesPath).Preferences.Theme);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            IdeSession.Settle();
        }

        Assert.False(CanvasTheme.IsLight);
    }

    private static Color ColorOf(IBrush brush) => ((ISolidColorBrush)brush).Color;

    // A translucent color drawn over an opaque one.
    private static Color Over(Color top, Color bottom)
    {
        double alpha = top.A / 255.0;
        byte Mix(byte a, byte b) => (byte)Math.Round((a * alpha) + (b * (1 - alpha)));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
}
