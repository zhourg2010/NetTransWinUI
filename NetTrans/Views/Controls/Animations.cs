using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace NetTrans.Views.Controls;

/// <summary>
/// The handoff uses a single easing curve, cubic-bezier(.32,.72,0,1), for every
/// transition. XAML expresses that exactly as a KeySpline, so these helpers
/// build spline key-frame storyboards rather than approximating with one of the
/// built-in easing functions.
/// </summary>
internal static class Animations
{
    /// <summary>cubic-bezier(.32,.72,0,1).</summary>
    internal static KeySpline Standard => new()
    {
        ControlPoint1 = new Point(0.32, 0.72),
        ControlPoint2 = new Point(0.0, 1.0),
    };

    /// <summary>Animates one double property of <paramref name="target"/> to <paramref name="to"/>.</summary>
    internal static Storyboard Slide(DependencyObject target, string property, double to, int milliseconds)
    {
        var frames = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        frames.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
            Value = to,
            KeySpline = Standard,
        });

        Storyboard.SetTarget(frames, target);
        Storyboard.SetTargetProperty(frames, property);

        var storyboard = new Storyboard();
        storyboard.Children.Add(frames);
        return storyboard;
    }

    /// <summary>The `snapIn` bounce played after a frame settles flush: .982 -> 1.006 -> 1.</summary>
    internal static Storyboard SnapIn(ScaleTransform scale)
    {
        var storyboard = new Storyboard();

        foreach (string property in new[] { "ScaleX", "ScaleY" })
        {
            var frames = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0.982 });
            frames.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(174)),
                Value = 1.006,
                KeySpline = Standard,
            });
            frames.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
                Value = 1.0,
                KeySpline = Standard,
            });

            Storyboard.SetTarget(frames, scale);
            Storyboard.SetTargetProperty(frames, property);
            storyboard.Children.Add(frames);
        }

        return storyboard;
    }

    /// <summary>Fades an element in over the given duration (the handoff's `fade` keyframes).</summary>
    internal static Storyboard Fade(UIElement target, double to, int milliseconds)
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
            Value = to,
            KeySpline = Standard,
        });

        Storyboard.SetTarget(frames, target);
        Storyboard.SetTargetProperty(frames, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(frames);
        return storyboard;
    }
}
