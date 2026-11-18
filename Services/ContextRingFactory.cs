using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UsageTools2026.Services;

/// <summary>
/// Builds a circular context-window gauge (background ring + colored arc + optional % label).
/// Shared by the main window dropdown and the mini window. Colors: blue &lt;70%, amber 70-90%, red ≥90%.
/// Uses fully-qualified WPF types because WinForms (System.Drawing) is also referenced project-wide.
/// </summary>
public static class ContextRingFactory
{
    public static FrameworkElement Build(double percent, double size, bool showLabel)
    {
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        var thickness = size * 0.14;
        var radius = (size - thickness) / 2;
        var center = size / 2;
        var colorHex = percent >= 90 ? "#FF5555" : percent >= 70 ? "#F3B05B" : "#4F9DF7";

        grid.Children.Add(new Ellipse
        {
            Width = size - thickness,
            Height = size - thickness,
            Stroke = SolidBrush("#27313A"),
            StrokeThickness = thickness,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });

        if (percent > 0)
        {
            var angle = System.Math.Min(359.999, percent / 100.0 * 360.0);
            var rad = (angle - 90) * System.Math.PI / 180.0;
            var figure = new PathFigure
            {
                StartPoint = new System.Windows.Point(center, center - radius),
                IsClosed = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = new System.Windows.Point(center + radius * System.Math.Cos(rad), center + radius * System.Math.Sin(rad)),
                Size = new System.Windows.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = angle > 180
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            grid.Children.Add(new Path
            {
                Data = geometry,
                Stroke = SolidBrush(colorHex),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        if (showLabel)
        {
            grid.Children.Add(new TextBlock
            {
                Text = $"{percent:0}%",
                FontSize = size * 0.24,
                FontWeight = FontWeights.Bold,
                Foreground = SolidBrush(colorHex),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
        }

        return grid;
    }

    private static System.Windows.Media.Brush SolidBrush(string hex) =>
        new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
