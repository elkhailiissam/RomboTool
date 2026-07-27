using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace RomboTool.Controls;

/// <summary>
/// A TextBlock that bolds/colours the matched substring inside a result preview.
/// Bound per-row in the results grid; rebuilds its inlines whenever the row recycles.
/// </summary>
public class HighlightPreview : TextBlock
{
    public static readonly StyledProperty<string?> PreviewProperty =
        AvaloniaProperty.Register<HighlightPreview, string?>(nameof(Preview));

    public static readonly StyledProperty<int> MatchStartProperty =
        AvaloniaProperty.Register<HighlightPreview, int>(nameof(MatchStart));

    public static readonly StyledProperty<int> MatchLengthProperty =
        AvaloniaProperty.Register<HighlightPreview, int>(nameof(MatchLength));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<HighlightPreview, IBrush?>(nameof(HighlightBrush));

    public string? Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public int MatchStart { get => GetValue(MatchStartProperty); set => SetValue(MatchStartProperty, value); }
    public int MatchLength { get => GetValue(MatchLengthProperty); set => SetValue(MatchLengthProperty, value); }
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PreviewProperty || change.Property == MatchStartProperty ||
            change.Property == MatchLengthProperty || change.Property == HighlightBrushProperty)
            Rebuild();
    }

    void Rebuild()
    {
        var text = Preview ?? "";
        var inlines = new InlineCollection();

        int s = MatchStart, l = MatchLength;
        if (l <= 0 || s < 0 || s >= text.Length)
        {
            inlines.Add(new Run(text));
        }
        else
        {
            if (s + l > text.Length) l = text.Length - s;
            if (s > 0) inlines.Add(new Run(text[..s]));
            inlines.Add(new Run(text.Substring(s, l))
            {
                Foreground = HighlightBrush ?? Brushes.Goldenrod,
                FontWeight = FontWeight.Bold,
            });
            int rest = s + l;
            if (rest < text.Length) inlines.Add(new Run(text[rest..]));
        }

        Inlines = inlines;
    }
}
