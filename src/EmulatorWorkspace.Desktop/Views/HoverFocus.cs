using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// Polish addendum item 10, first half: in the device card grid, hovering one
/// card dims every sibling (the dashed "New device" tile included) to 82 %
/// over 120 ms on cubic-bezier(.2,.8,.2,1), and the hovered card stays at
/// 100 %. Presentation only: the behaviour reads the pointer and sets opacity
/// on item containers the ItemsControl already created. It never touches a view
/// model, the supervisor or the guest.
/// </summary>
public static class CardHoverFocus
{
    /// <summary>Sibling opacity while one card carries the pointer.</summary>
    public const double DimmedOpacity = 0.82;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(CardHoverFocus),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement card)
        {
            return;
        }

        if (e.NewValue is true)
        {
            card.MouseEnter += OnMouseEnter;
            card.MouseLeave += OnMouseLeave;
            card.Unloaded += OnUnloaded;
        }
        else
        {
            card.MouseEnter -= OnMouseEnter;
            card.MouseLeave -= OnMouseLeave;
            card.Unloaded -= OnUnloaded;
            Restore(card);
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not DependencyObject card)
        {
            return;
        }

        var host = FindHost(card);
        if (host is null)
        {
            return;
        }

        var hovered = FindContainer(card);
        foreach (var container in Containers(host))
        {
            if (ReferenceEquals(container, hovered))
            {
                continue;
            }

            Animate(container, DimmedOpacity);
        }
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e) => Restore(sender as DependencyObject);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Restore(sender as DependencyObject);

    private static void Restore(DependencyObject? card)
    {
        if (card is null)
        {
            return;
        }

        var host = FindHost(card);
        if (host is null)
        {
            return;
        }

        foreach (var container in Containers(host))
        {
            Animate(container, 1.0);
        }
    }

    private static void Animate(UIElement target, double opacity)
    {
        // Reduced motion: land on the value with no interpolation at all.
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(UIElement.OpacityProperty, null);
            target.Opacity = opacity;
            return;
        }

        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(Duration) };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            opacity,
            KeyTime.FromTimeSpan(Duration),
            new KeySpline(0.2, 0.8, 0.2, 1)));
        target.BeginAnimation(UIElement.OpacityProperty, timeline, HandoffBehavior.SnapshotAndReplace);
    }

    private static ItemsControl? FindHost(DependencyObject start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is ItemsControl control)
            {
                return control;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>The item container (ContentPresenter) that hosts this card.</summary>
    private static DependencyObject? FindContainer(DependencyObject start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is ContentPresenter presenter)
            {
                return presenter;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static IEnumerable<UIElement> Containers(ItemsControl host)
    {
        for (var index = 0; index < host.Items.Count; index++)
        {
            if (host.ItemContainerGenerator.ContainerFromIndex(index) is UIElement container)
            {
                yield return container;
            }
        }
    }
}

/// <summary>
/// Polish addendum item 10, second half: a rail icon scales to 1.07 on hover
/// with a soft accent-soft radial fading in behind it, over 120 ms. Local to
/// this window's rail; the shared-template version is UX-04b. The template
/// supplies the two parts (<c>PART_Glow</c> and <c>PART_Glyph</c>), and the
/// motion is skipped entirely when Windows reports reduced motion.
/// </summary>
public static class RailHover
{
    public const double HoverScale = 1.07;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(RailHover),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control button)
        {
            return;
        }

        if (e.NewValue is true)
        {
            button.Loaded += OnLoaded;
            button.Unloaded += OnUnloaded;
        }
        else
        {
            button.Loaded -= OnLoaded;
            button.Unloaded -= OnUnloaded;
            button.MouseEnter -= OnMouseEnter;
            button.MouseLeave -= OnMouseLeave;
            Apply(button, hovered: false);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control button)
        {
            return;
        }

        button.MouseEnter -= OnMouseEnter;
        button.MouseLeave -= OnMouseLeave;
        button.MouseEnter += OnMouseEnter;
        button.MouseLeave += OnMouseLeave;
        Apply(button, hovered: false);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control button)
        {
            Apply(button, hovered: false);
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e) => Apply(sender as Control, hovered: true);

    private static void OnMouseLeave(object sender, MouseEventArgs e) => Apply(sender as Control, hovered: false);

    private static void Apply(Control? button, bool hovered)
    {
        if (button?.Template is null)
        {
            return;
        }

        if (button.Template.FindName("PART_Glow", button) is UIElement glow)
        {
            Fade(glow, hovered ? 1d : 0d);
        }

        if (button.Template.FindName("PART_Glyph", button) is FrameworkElement glyph)
        {
            Scale(glyph, hovered ? HoverScale : 1d);
        }
    }

    private static void Fade(UIElement target, double opacity)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(UIElement.OpacityProperty, null);
            target.Opacity = opacity;
            return;
        }

        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(Duration) };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            opacity,
            KeyTime.FromTimeSpan(Duration),
            new KeySpline(0.2, 0.8, 0.2, 1)));
        target.BeginAnimation(UIElement.OpacityProperty, timeline, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Scale(FrameworkElement glyph, double scale)
    {
        // A template transform can arrive frozen; clone it once so the
        // animation has a mutable instance to drive.
        if (glyph.RenderTransform is not ScaleTransform transform || transform.IsFrozen)
        {
            transform = new ScaleTransform(1, 1);
            glyph.RenderTransform = transform;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = scale;
            transform.ScaleY = scale;
            return;
        }

        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(Duration) };
        var keyTime = KeyTime.FromTimeSpan(Duration);
        var spline = new KeySpline(0.2, 0.8, 0.2, 1);
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(scale, keyTime, spline));
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, timeline, HandoffBehavior.SnapshotAndReplace);

        var vertical = new DoubleAnimationUsingKeyFrames { Duration = new Duration(Duration) };
        vertical.KeyFrames.Add(new SplineDoubleKeyFrame(scale, keyTime, new KeySpline(0.2, 0.8, 0.2, 1)));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, vertical, HandoffBehavior.SnapshotAndReplace);
    }
}
