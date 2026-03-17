using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JsonPad.Ui;

public sealed class LineNumberGutter : FrameworkElement
{
    public TextBox? TargetTextBox { get; set; }

    public Brush LineNumberBrush { get; set; } = Brushes.DimGray;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (TargetTextBox is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var first = TargetTextBox.GetFirstVisibleLineIndex();
        var last = TargetTextBox.GetLastVisibleLineIndex();
        if (first < 0 || last < first)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            TargetTextBox.FontFamily,
            TargetTextBox.FontStyle,
            TargetTextBox.FontWeight,
            TargetTextBox.FontStretch);

        for (var line = first; line <= last; line++)
        {
            var charIndex = TargetTextBox.GetCharacterIndexFromLineIndex(line);
            if (charIndex < 0)
            {
                continue;
            }

            var rect = TargetTextBox.GetRectFromCharacterIndex(charIndex, trailingEdge: true);
            if (rect.IsEmpty)
            {
                continue;
            }

            var text = new FormattedText(
                (line + 1).ToString(CultureInfo.InvariantCulture),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                TargetTextBox.FontSize,
                LineNumberBrush,
                dpi);

            drawingContext.DrawText(text, new Point(ActualWidth - text.Width - 6, rect.Top));
        }
    }
}
