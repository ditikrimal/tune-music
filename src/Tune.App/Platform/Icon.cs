using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Tune.App.Platform;

/// <summary>
/// A vector icon drawn from a 24×24 path (Material-style, see the Ico* geometries in Theme.xaml).
/// Takes its colour from the surrounding Foreground, so button hover/accent states just work.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Icon()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is null) return;
        var s = Size / 24.0;
        dc.PushTransform(new ScaleTransform(s, s));
        dc.DrawGeometry(Foreground, null, Data);
        dc.Pop();
    }
}

/// <summary>
/// The "like" pop: when a button's Tag turns "On" because the user just clicked it, it bounces and glows in
/// the accent colour. Rows that scroll into view already liked don't animate.
/// </summary>
public static class LikePop
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(LikePop), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    private static readonly DependencyPropertyDescriptor TagDescriptor =
        DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(FrameworkElement));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b || e.NewValue is not true) return;
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        TagDescriptor.AddValueChanged(b, (_, _) =>
        {
            // only a click the user just made (pointer on it), not a recycled row or a refresh
            if (b.Tag as string == "On" && b.IsLoaded && b.IsMouseOver) Play(b);
        });
    }

    private static void Play(ButtonBase b)
    {
        var scale = new ScaleTransform(1, 1);
        b.RenderTransform = scale;
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(270))));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new BackEase { EasingMode = EasingMode.EaseOut }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);

        var color = Application.Current.TryFindResource("AccentColor") is Color c ? c : Colors.White;
        var glow = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 18, Opacity = 0 };
        b.Effect = glow;
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(600) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))));
        fade.Completed += (_, _) => b.Effect = null;
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
    }
}
