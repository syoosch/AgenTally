using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgenTally.UI.Controls;

public sealed class RoundedClipBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arrangedSize = base.ArrangeOverride(finalSize);
        ApplyChildClip();
        return arrangedSize;
    }

    private void ApplyChildClip()
    {
        if (Child is not UIElement child)
        {
            return;
        }

        double borderInset = Math.Max(
            Math.Max(BorderThickness.Left, BorderThickness.Top),
            Math.Max(BorderThickness.Right, BorderThickness.Bottom));
        double radius = Math.Max(
            0d,
            Math.Min(
                Math.Min(CornerRadius.TopLeft, CornerRadius.TopRight),
                Math.Min(CornerRadius.BottomRight, CornerRadius.BottomLeft)) -
            borderInset);
        var clipBounds = new Rect(child.RenderSize);
        if (child.Clip is RectangleGeometry clip)
        {
            clip.Rect = clipBounds;
            clip.RadiusX = radius;
            clip.RadiusY = radius;
            return;
        }

        child.Clip = new RectangleGeometry(clipBounds, radius, radius);
    }
}
