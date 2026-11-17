using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimaWindows.App.Controls;

/// <summary>Poster with title, optional caption and optional progress bar (0..100; negative hides it).</summary>
public sealed partial class PosterCard : UserControl
{
    public static readonly DependencyProperty CoverUrlProperty =
        DependencyProperty.Register(nameof(CoverUrl), typeof(string), typeof(PosterCard), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PosterCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(PosterCard), new PropertyMetadata(null));

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(PosterCard), new PropertyMetadata(-1.0));

    public PosterCard() => InitializeComponent();

    public string? CoverUrl
    {
        get => (string?)GetValue(CoverUrlProperty);
        set => SetValue(CoverUrlProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Caption
    {
        get => (string?)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>The poster image area, used as the connected-animation source.</summary>
    public UIElement PosterElement => PosterHost;
}
