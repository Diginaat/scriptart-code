using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ScriptArt.App;

/// <summary>
/// Colors set in code rather than in Theme.axaml. Icon colors are mid-tones that keep 3:1 contrast on the panels of both themes;
/// syntax and diff colors have a dark and a light variant (D-046).
/// </summary>
internal static class IdeColors
{
    public static readonly IBrush IconGreen = Brush("#2F8E3E");
    public static readonly IBrush IconGold = Brush("#A87A0A");
    public static readonly IBrush IconPurple = Brush("#8E6BD8");
    public static readonly IBrush IconOrchid = Brush("#B455B4");
    public static readonly IBrush IconBlue = Brush("#4682B4");
    public static readonly IBrush IconSky = Brush("#3A88BD");
    public static readonly IBrush IconKhaki = Brush("#937A26");
    public static readonly IBrush IconGray = Brush("#7D838C");

    /// <summary>The syntax color for an AvaloniaEdit highlighting color name, on the code background of the theme.</summary>
    public static string SyntaxColor(string name, bool light) => name switch
    {
        _ when Has(name, "Comment") => light ? "#3B7A3B" : "#6A9955",
        _ when Has(name, "String") || Has(name, "Char") || Has(name, "AttributeValue") => light ? "#A31515" : "#CE9178",
        _ when Has(name, "AttributeName") || Has(name, "Property") => light ? "#0451A5" : "#9CDCFE",
        _ when Has(name, "Number") || Has(name, "Digits") => light ? "#09774D" : "#B5CEA8",
        _ when Has(name, "Method") => light ? "#795E26" : "#DCDCAA",
        _ when Has(name, "Preprocessor") || Has(name, "Declaration") || Has(name, "CData") => light ? "#6B6B6B" : "#9B9B9B",
        _ when Has(name, "Heading") || Has(name, "Emphasis") => light ? "#795E26" : "#DCDCAA",
        _ => light ? "#0000D0" : "#569CD6",
    };

    /// <summary>Unified diff colors: added and removed lines (text on a tinted line) and hunk headers.</summary>
    public static (IBrush Added, IBrush AddedBackground, IBrush Removed, IBrush RemovedBackground, IBrush Header) Diff(bool light) => light
        ? (Brush("#1E7B34"), Brush("#E6F4EA"), Brush("#B42318"), Brush("#FCE8E8"), Brush("#1F5FBF"))
        : (Brush("#7FD68F"), Brush("#1F3A27"), Brush("#F08A8A"), Brush("#3F2326"), Brush("#6CA6FF"));

    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static ImmutableSolidColorBrush Brush(string color) => new(Color.Parse(color));
}
