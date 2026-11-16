using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// Polish addendum item 4, global half: the shared button, icon-button and
/// list-item templates carry two overlay layers named <c>PART_Hover</c> and
/// <c>PART_Press</c>. This behaviour fades those layers instead of swapping the
/// surface colour, so hover runs over 120 ms and press over 80 ms on
/// cubic-bezier(.2,.8,.2,1) exactly as the design guide asks, without a colour
/// animation on a shared (and therefore frozen) resource brush.
///
/// A template opts in with <c>views:Motion.HoverFade="True"</c>; the shared
/// styles already set it. When Windows reports reduced motion the layers land on
/// their value with no interpolation at all.
/// </summary>
public static class Motion
{
    /// <summary>Hover fade, design guide: 120 ms.</summary>
    public const int HoverMilliseconds = 120;

    /// <summary>Press feedback, design guide: 80 ms in, 120 ms out.</summary>
    public const int PressMilliseconds = 80;

    private const string HoverPart = "PART_Hover";
    private const string PressPart = "PART_Press";

    private static readonly DependencyPropertyDescriptor? MouseOverDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.IsMouseOverProperty, typeof(UIElement));

    public static readonly DependencyProperty HoverFadeProperty = DependencyProperty.RegisterAttached(
        "HoverFade",
        typeof(bool),
        typeof(Motion),
        new PropertyMetadata(false, OnHoverFadeChanged));

    /// <summary>
    /// UX-14: the maximize button hands its rectangle to the window manager
    /// (HTMAXBUTTON) so Windows 11 can show the Snap Layouts flyout, and WPF then
    /// never raises IsMouseOver or IsPressed for it. The title bar feeds these two
    /// flags from the non-client messages instead, and the layers fade with the
    /// same 120/80 ms curves and the same reduced-motion rule.
    /// </summary>
    public static readonly DependencyProperty CaptionHotProperty = DependencyProperty.RegisterAttached(
        "CaptionHot",
        typeof(bool),
        typeof(Motion),
        new PropertyMetadata(false, OnCaptionStateChanged));

    public static readonly DependencyProperty CaptionPressedProperty = DependencyProperty.RegisterAttached(
        "CaptionPressed",
        typeof(bool),
        typeof(Motion),
        new PropertyMetadata(false, OnCaptionStateChanged));

    public static void SetCaptionHot(DependencyObject element, bool value) =>
        element.SetValue(CaptionHotProperty, value);

    public static bool GetCaptionHot(DependencyObject element) =>
        (bool)element.GetValue(CaptionHotProperty);

    public static void SetCaptionPressed(DependencyObject element, bool value) =>
        element.SetValue(CaptionPressedProperty, value);

    public static bool GetCaptionPressed(DependencyObject element) =>
        (bool)element.GetValue(CaptionPressedProperty);

    public static void SetHoverFade(DependencyObject element, bool value) =>
        element.SetValue(HoverFadeProperty, value);

    public static bool GetHoverFade(DependencyObject element) =>
        (bool)element.GetValue(HoverFadeProperty);

    private static void OnHoverFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                Attach(element);
            }
        }
        else
        {
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
            Detach(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Attach(element);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Detach(element);
        }
    }

    private static void Attach(FrameworkElement element)
    {
        if (MouseOverDescriptor is not null)
        {
            MouseOverDescriptor.RemoveValueChanged(element, OnMouseOverChanged);
            MouseOverDescriptor.AddValueChanged(element, OnMouseOverChanged);
        }

        if (element is ButtonBase button)
        {
            var pressed = DependencyPropertyDescriptor.FromProperty(
                ButtonBase.IsPressedProperty, typeof(ButtonBase));
            pressed?.RemoveValueChanged(element, OnPressedChanged);
            pressed?.AddValueChanged(element, OnPressedChanged);
            SetLayer(element, PressPart, IsPressed(button) ? 1d : 0d, animate: false);
        }

        SetLayer(element, HoverPart, IsHot(element) ? 1d : 0d, animate: false);
    }

    private static void Detach(FrameworkElement element)
    {
        MouseOverDescriptor?.RemoveValueChanged(element, OnMouseOverChanged);
        if (element is ButtonBase)
        {
            DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase))
                ?.RemoveValueChanged(element, OnPressedChanged);
        }
    }

    private static void OnMouseOverChanged(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            SetLayer(element, HoverPart, IsHot(element) ? 1d : 0d, animate: true);
        }
    }

    private static void OnPressedChanged(object? sender, EventArgs e)
    {
        if (sender is ButtonBase button)
        {
            SetLayer(button, PressPart, IsPressed(button) ? 1d : 0d, animate: true);
        }
    }

    private static void OnCaptionStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        SetLayer(element, HoverPart, IsHot(element) ? 1d : 0d, animate: true);
        if (element is ButtonBase button)
        {
            SetLayer(element, PressPart, IsPressed(button) ? 1d : 0d, animate: true);
        }
    }

    /// <summary>
    /// Hover is the pointer on the control or the non-client hot flag the title
    /// bar sets while the window manager owns the mouse over a caption button.
    /// </summary>
    private static bool IsHot(FrameworkElement element) =>
        element.IsMouseOver || GetCaptionHot(element);

    private static bool IsPressed(ButtonBase button) =>
        button.IsPressed || GetCaptionPressed(button);

    private static void SetLayer(FrameworkElement element, string part, double opacity, bool animate)
    {
        if (FindPart(element, part) is not UIElement layer)
        {
            return;
        }

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.Opacity = opacity;
            return;
        }

        var milliseconds = part == PressPart
            ? (opacity > 0 ? PressMilliseconds : HoverMilliseconds)
            : HoverMilliseconds;

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            opacity,
            KeyTime.FromTimeSpan(duration),
            new KeySpline(0.2, 0.8, 0.2, 1)));
        layer.BeginAnimation(UIElement.OpacityProperty, timeline, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// The named layer inside the applied template. Templates that do not define
    /// the part simply do not animate, which keeps the behaviour optional.
    /// </summary>
    private static UIElement? FindPart(FrameworkElement element, string name)
    {
        if (element is Control { Template: { } template }
            && template.FindName(name, (Control)element) is UIElement named)
        {
            return named;
        }

        return Search(element, name);
    }

    private static UIElement? Search(DependencyObject start, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(start);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(start, index);
            if (child is FrameworkElement candidate && candidate.Name == name)
            {
                return candidate;
            }

            if (Search(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
