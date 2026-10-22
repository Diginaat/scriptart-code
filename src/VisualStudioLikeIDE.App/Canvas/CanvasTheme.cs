using Avalonia.Media;
using Avalonia.Media.Immutable;
using VisualStudioLikeIDE.Compiler.TypeSystem;

namespace VisualStudioLikeIDE.App.Canvas;

/// <summary>
/// Colors and metrics for the node canvas. Colors reinforce meaning but are never the only cue:
/// execution and data pins have different shapes, and data pins are labeled with their type.
/// </summary>
internal static class CanvasTheme
{
    public static readonly IBrush Background = Brush("#1A1B1E");
    public static readonly IPen GridMinor = new ImmutablePen(Brush("#222429"), 1);
    public static readonly IPen GridMajor = new ImmutablePen(Brush("#2B2E34"), 1);

    public static readonly IBrush NodeBody = Brush("#26282D");
    public static readonly IPen NodeBorder = new ImmutablePen(Brush("#3A3D44"), 1);
    public static readonly IPen NodeSelected = new ImmutablePen(Brush("#F2B33D"), 2);
    public static readonly IPen NodeError = new ImmutablePen(Brush("#F06A6A"), 2);
    public static readonly IPen NodeWarning = new ImmutablePen(Brush("#E8B44C"), 1.5);
    public static readonly IBrush Title = Brush("#F2F3F5");
    public static readonly IBrush Label = Brush("#C9CCD2");
    public static readonly IBrush TypeLabel = Brush("#7E838C");
    public static readonly IBrush ValuePreview = Brush("#E3C08D");
    public static readonly IBrush Execution = Brush("#E8E9EC");
    public static readonly IBrush Marquee = Brush("#304C8DFF");
    public static readonly IPen MarqueeBorder = new ImmutablePen(Brush("#4C8DFF"), 1);
    public static readonly IBrush Valid = Brush("#5CCB7A");
    public static readonly IBrush MinimapBackground = Brush("#E61E2024");
    public static readonly IPen MinimapBorder = new ImmutablePen(Brush("#3A3D44"), 1);
    public static readonly IBrush MinimapComment = Brush("#404A5568");
    public static readonly IPen MinimapView = new ImmutablePen(Brush("#F2B33D"), 1.5);
    public static readonly IBrush CommentBody = Brush("#2E4A5568");
    public static readonly IBrush CommentHeader = Brush("#804A5568");
    public static readonly IPen CommentBorder = new ImmutablePen(Brush("#5A6577"), 1);
    public static readonly IPen CommentGrip = new ImmutablePen(Brush("#8A93A3"), 1.5);
    public static readonly IBrush Invalid = Brush("#F06A6A");

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

    private static readonly IBrush DefaultData = Brush("#9AA0A6");
    private static readonly Dictionary<TypeRef, IBrush> DataColors = new()
    {
        [TypeRef.Boolean] = Brush("#D9534F"),
        [TypeRef.Int32] = Brush("#3DC6B5"),
        [TypeRef.Double] = Brush("#8BD45A"),
        [TypeRef.String] = Brush("#E05CC8"),
    };

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

    public static IBrush ForType(TypeRef? type) =>
        type is null ? Execution : DataColors.GetValueOrDefault(type, DefaultData);

    public static IBrush ForCategory(string category) => CategoryColors.GetValueOrDefault(category, DefaultCategory);

    private static ImmutableSolidColorBrush Brush(string color) => new(Color.Parse(color));
}
