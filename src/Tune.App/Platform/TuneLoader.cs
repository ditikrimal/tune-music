using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tune.App.Platform;

/// <summary>
/// Tune's loading animation: the logo's six bars bouncing like an equalizer, on a transparent background.
/// Animates only while visible (no CPU when hidden). Colour = Foreground (default: the accent).
/// </summary>
public sealed class TuneLoader : Grid
{
    private readonly ScaleTransform[] _scales = new ScaleTransform[6];
    private readonly Storyboard _story = new() { RepeatBehavior = RepeatBehavior.Forever };

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(TuneLoader),
        new PropertyMetadata(null, (d, e) => ((TuneLoader)d).Paint()));

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public TuneLoader()
    {
        Width = 36;
        Height = 28;
        for (var i = 0; i < 6; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition());
            var scale = new ScaleTransform(1, 0.3);
            _scales[i] = scale;
            var bar = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(1.5, 0, 1.5, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
            SetColumn(bar, i);
            Children.Add(bar);
            // a wave travelling left to right; never fully flat
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100), BeginTime = TimeSpan.FromMilliseconds(i * 150 - 1100) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.25, KeyTime.FromPercent(0)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.5), new SineEase { EasingMode = EasingMode.EaseInOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.25, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }));
            a.RepeatBehavior = RepeatBehavior.Forever;
            Storyboard.SetTarget(a, bar);
            Storyboard.SetTargetProperty(a, new PropertyPath("RenderTransform.ScaleY"));
            _story.Children.Add(a);
        }
        Paint();
        IsVisibleChanged += (_, _) => Run();
        Loaded += (_, _) => { Paint(); Run(); };
        Unloaded += (_, _) => _story.Stop(this);
    }

    private void Run()
    {
        if (IsVisible && IsLoaded) _story.Begin(this, isControllable: true);
        else _story.Stop(this);
    }

    private void Paint()
    {
        foreach (var b in Children.OfType<Border>())
        {
            if (Foreground is { } brush) b.Background = brush;
            else b.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); // follows accent changes
        }
    }
}
