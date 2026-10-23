using Avalonia.Media;
using Avalonia.Media.Immutable;
using ScriptArt.Compiler.TypeSystem;

namespace ScriptArt.App.Canvas;

/// <summary>
/// Colors and metrics for the node canvas. Colors reinforce meaning but are never the only cue:
/// execution and data pins have different shapes, and data pins are labeled with their type.
/// </summary>
internal static class CanvasTheme
{
    // Dark and light values of each color. Text and its background meet WCAG AA (4.5:1), pins and outlines 3:1; node titles are
    // light text on the category colors in both themes. ThemeContrastTests checks the pairs.
    public static bool IsLight { get; private set; }

    public static IBrush Background { get; private set; } = null!;
    public static IPen GridMinor { get; private set; } = null!;
    public static IPen GridMajor { get; private set; } = null!;

    public static IBrush NodeBody { get; private set; } = null!;
    public static IPen NodeBorder { get; private set; } = null!;
    public static IPen NodeSelected { get; private set; } = null!;
    public static IPen NodeError { get; private set; } = null!;
    public static IPen NodeWarning { get; private set; } = null!;
    public static IBrush Title { get; private set; } = null!;
    public static IBrush Label { get; private set; } = null!;
    public static IBrush TypeLabel { get; private set; } = null!;
    public static IBrush ValuePreview { get; private set; } = null!;
    public static IBrush Execution { get; private set; } = null!;
    public static IBrush Marquee { get; private set; } = null!;
    public static IPen MarqueeBorder { get; private set; } = null!;
    public static IBrush Valid { get; private set; } = null!;
    public static IBrush MinimapBackground { get; private set; } = null!;
    public static IPen MinimapBorder { get; private set; } = null!;
    public static IBrush MinimapComment { get; private set; } = null!;
    public static IPen MinimapView { get; private set; } = null!;
    public static IBrush OverlayText { get; private set; } = null!;
    public static IBrush CommentBody { get; private set; } = null!;
    public static IBrush CommentHeader { get; private set; } = null!;
    public static IBrush CommentTitle { get; private set; } = null!;
    public static IPen CommentBorder { get; private set; } = null!;
    public static IPen CommentGrip { get; private set; } = null!;
    public static IBrush Invalid { get; private set; } = null!;
    public static IBrush Breakpoint { get; private set; } = null!;
    public static IPen BreakpointBorder { get; private set; } = null!;
    public static IPen ExecutionOutline { get; private set; } = null!;

    public const double HeaderHeight = 28;
    public const double RowHeight = 24;
    public const double PreviewHeight = 22;
    public const double CornerRadius = 6;
    public const double PinRadius = 5;
    public const double PinHitRadius = 10;
    public const double RerouteRadius = 5;
    public const double WireHitDistance = 6;
    public const double MinNodeWidth = 150;
    public const double CommentHeaderHeight = 30;
    public const double CommentFontSize = 14;
    public const double CommentMinWidth = 160;
    public const double CommentMinHeight = 80;
    public const double ResizeGrip = 16;
    public const double MinimapWidth = 220;
    public const double MinimapHeight = 140;
    public const double MinimapMargin = 12;
    public const double FontSize = 12;
    public const double SmallFontSize = 11;

    public static readonly Typeface Typeface = new(FontFamily.Default);
    public static readonly Typeface TitleTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private static IBrush defaultData = null!;
    private static Dictionary<TypeRef, IBrush> dataColors = [];

    // Header colors behind light title text; the same in both themes.
    private static readonly IBrush DefaultCategory = Brush("#5A6270");
    private static readonly Dictionary<string, IBrush> CategoryColors = new(StringComparer.Ordinal)
    {
        ["Flow"] = Brush("#8A3B3B"),
        ["Console"] = Brush("#2F5D8A"),
        ["Literals"] = Brush("#5C5470"),
        ["Math"] = Brush("#3D6B4F"),
        ["Comparison"] = Brush("#6B5A2E"),
        ["Logic"] = Brush("#7A3F5E"),
        ["String"] = Brush("#6E3F7A"),
        ["Conversion"] = Brush("#3F5F6E"),
        ["Variables"] = Brush("#2E6B6B"),
    };

    static CanvasTheme() => Use(light: false);

    /// <summary>Switches every color to the dark or the light palette. The canvas rebuilds its layouts afterwards.</summary>
    public static void Use(bool light)
    {
        IsLight = light;
        ImmutableSolidColorBrush Pick(string dark, string lightColor) => Brush(light ? lightColor : dark);
        IPen Pen(string dark, string lightColor, double thickness) => new ImmutablePen(Pick(dark, lightColor), thickness);

        Background = Pick("#1A1B1E", "#F5F6F8");
        GridMinor = Pen("#222429", "#EBEDF0", 1);
        GridMajor = Pen("#2B2E34", "#DDE0E5", 1);
        NodeBody = Pick("#26282D", "#FFFFFF");
        NodeBorder = Pen("#3A3D44", "#C4C8D0", 1);
        NodeSelected = Pen("#F2B33D", "#C27C00", 2);
        NodeError = Pen("#F06A6A", "#C42B2B", 2);
        NodeWarning = Pen("#E8B44C", "#8A5A00", 1.5);
        Title = Brush("#F2F3F5");
        Label = Pick("#C9CCD2", "#2B2F36");
        TypeLabel = Pick("#959AA3", "#5B616B");
        ValuePreview = Pick("#E3C08D", "#8A5A00");
        Execution = Pick("#E8E9EC", "#3A3F47");
        Marquee = Brush("#304C8DFF");
        MarqueeBorder = Pen("#4C8DFF", "#1F6FEB", 1);
        Valid = Pick("#5CCB7A", "#1E7B34");
        MinimapBackground = Pick("#E61E2024", "#F2FFFFFF");
        MinimapBorder = Pen("#3A3D44", "#C4C8D0", 1);
        MinimapComment = Brush("#404A5568");
        MinimapView = Pen("#F2B33D", "#C27C00", 1.5);
        OverlayText = Pick("#F2F3F5", "#1F2329");
        CommentBody = Pick("#2E4A5568", "#224A5568");
        CommentHeader = Pick("#804A5568", "#604A5568");
        CommentTitle = Pick("#F2F3F5", "#1F2329");
        CommentBorder = Pen("#5A6577", "#8A93A3", 1);
        CommentGrip = Pen("#8A93A3", "#5A6577", 1.5);
        Invalid = Pick("#F06A6A", "#C42B2B");
        Breakpoint = Brush("#E5484D");
        BreakpointBorder = Pen("#F2F3F5", "#FFFFFF", 1);
        ExecutionOutline = Pen("#F5D547", "#C27C00", 3);
        defaultData = Pick("#9AA0A6", "#6B7178");
        dataColors = new()
        {
            [TypeRef.Boolean] = Pick("#D9534F", "#C9302C"),
            [TypeRef.Int32] = Pick("#3DC6B5", "#0F8577"),
            [TypeRef.Double] = Pick("#8BD45A", "#4A8A1C"),
            [TypeRef.String] = Pick("#E05CC8", "#B5329E"),
        };
    }

    public static IBrush ForType(TypeRef? type) =>
        type is null ? Execution : dataColors.GetValueOrDefault(type, defaultData);

    public static IBrush ForCategory(string category) => CategoryColors.GetValueOrDefault(category, DefaultCategory);

    private static ImmutableSolidColorBrush Brush(string color) => new(Color.Parse(color));
}
