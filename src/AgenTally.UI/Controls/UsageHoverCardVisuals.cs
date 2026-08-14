using System.Windows;
using System.Windows.Media;

namespace AgenTally.UI.Controls;

internal static class UsageHoverCardVisuals
{
    internal const double CornerRadius = 12;
    internal const double ShadowOffset = 3;
    private static readonly Brush ShadowBrush = FrozenBrush("#18292621");

    internal static void DrawSurface(
        DrawingContext drawingContext,
        Rect card,
        Brush background,
        Brush border)
    {
        var shadow = new Rect(
            card.X,
            card.Y + ShadowOffset,
            card.Width,
            card.Height);
        drawingContext.DrawRoundedRectangle(
            ShadowBrush,
            null,
            shadow,
            CornerRadius,
            CornerRadius);
        drawingContext.DrawRoundedRectangle(
            background,
            new Pen(border, 1),
            card,
            CornerRadius,
            CornerRadius);
    }

    private static Brush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
