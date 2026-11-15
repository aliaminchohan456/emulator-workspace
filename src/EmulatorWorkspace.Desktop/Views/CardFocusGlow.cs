using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// igr-feedback-ux04b item 2: the token ring (the card's accent border) stays the
/// default focus cue, and the single focused card additionally carries a real
/// glow. The glow is a <see cref="DropShadowEffect"/> at ShadowDepth 0 and
/// BlurRadius 18 in the theme accent; its opacity weight comes from
/// <c>FocusGlowColor</c>/<c>FocusGlowOpacity</c> in each theme dictionary (.35
/// dark, .25 light, 0 in High Contrast).
///
/// Only the effect's Opacity is animated, 0 to the theme weight over 120 ms on
/// cubic-bezier(.2,.8,.2,1). The blur radius is never animated, and reduced
/// motion lands on the weight with no interpolation. Unfocused cards carry no
/// effect at all, so at most one glow exists at any moment.
/// </summary>
public static class CardFocusGlow
{
    public const double GlowBlurRadius = 18;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(CardFocusGlow),
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
            card.IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
            card.Unloaded += OnUnloaded;
            Apply(card, card.IsKeyboardFocusWithin);
        }
        else
        {
            card.IsKeyboardFocusWithinChanged -= OnFocusWithinChanged;
            card.Unloaded -= OnUnloaded;
            Apply(card, false);
        }
    }

    private static void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Apply(sender as FrameworkElement, e.NewValue is true);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Apply(sender as FrameworkElement, false);

    private static void Apply(FrameworkElement? card, bool focused)
    {
        if (card is null)
        {
            return;
        }

        if (!focused)
        {
            if (card.Effect is DropShadowEffect current)
            {
                current.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            }

            card.Effect = null;
            return;
        }

        var accent = card.TryFindResource("FocusGlowColor") is Color color ? color : Colors.Transparent;
        var weight = card.TryFindResource("FocusGlowOpacity") is double value ? value : 0d;

        if (card.Effect is not DropShadowEffect effect)
        {
            effect = new DropShadowEffect
            {
                ShadowDepth = 0,
                Direction = 270,
                BlurRadius = GlowBlurRadius,
                Color = accent,
                Opacity = 0,
            };
            card.Effect = effect;
        }
        else
        {
            effect.Color = accent;
        }

        if (!SystemParameters.ClientAreaAnimation || weight <= 0)
        {
            effect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            effect.Opacity = weight;
            return;
        }

        var timeline = new DoubleAnimationUsingKeyFrames { Duration = new Duration(Duration) };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            weight,
            KeyTime.FromTimeSpan(Duration),
            new KeySpline(0.2, 0.8, 0.2, 1)));
        effect.BeginAnimation(DropShadowEffect.OpacityProperty, timeline, HandoffBehavior.SnapshotAndReplace);
    }
}
