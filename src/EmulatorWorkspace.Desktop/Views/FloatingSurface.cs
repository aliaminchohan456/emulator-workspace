using System.Windows;
using System.Windows.Controls;

namespace EmulatorWorkspace.Desktop.Views;

/// <summary>
/// Polish addendum item 3, global half: a floating surface (dialog, palette,
/// toast, menu) is the only place the design allows depth. The template layers
/// a wide ambient shadow, a tight key shadow and a 1 px specular edge whose
/// highlight runs from the top edge down, so the same surface serves every
/// floating panel and the treatment is defined once.
/// </summary>
public class FloatingSurface : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(CornerRadius),
        typeof(FloatingSurface),
        new PropertyMetadata(new CornerRadius(12)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static FloatingSurface()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FloatingSurface),
            new FrameworkPropertyMetadata(typeof(FloatingSurface)));
    }
}
