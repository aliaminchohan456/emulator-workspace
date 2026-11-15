using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using EmulatorWorkspace.Desktop.ViewModels;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// UX-09: the privileged consent surface shared by every consequential root
/// action - the Magisk lab install, ADB root and stock recovery. It replaces the
/// OS MessageBox, shows exactly what the actor named (this guest, these files,
/// these consequences) and returns the same bool contract the MessageBox did.
/// The window answers true only when the operator acknowledges the change, types
/// the guest name when the action asks for it, and presses the danger-styled
/// confirm; anything else (Cancel, Esc, the scrim, closing the window) returns
/// without a DialogResult, so the caller never sends its first supervisor
/// request and no transaction or journal entry exists to clean up.
/// </summary>
public partial class ConsentDialogWindow : Window
{
    private const int EnterMilliseconds = 160;
    private const int ExitMilliseconds = 140;

    private readonly ConsentDialogModel _model;
    private bool _closing;

    public ConsentDialogWindow(ConsentDialogModel model)
    {
        InitializeComponent();
        Services.ThemeManager.Initialize();
        Services.Chrome.Initialize();
        Services.Chrome.Apply(this);
        _model = model;
        DataContext = model;
        Title = model.ConfirmLabel;
        AckBox.Checked += (_, _) => UpdateConfirm();
        AckBox.Unchecked += (_, _) => UpdateConfirm();
        ConfirmBox.TextChanged += (_, _) => UpdateConfirm();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        UpdateConfirm();
    }

    /// <summary>
    /// The dialog covers the owner's client area so the scrim dims the studio and
    /// nothing else, then the card animates in. Keyboard focus starts on Cancel: a
    /// stray Enter on a destructive dialog should do nothing at all.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOverOwner();
        Card.MaxHeight = Math.Max(380, Height - 32);
        CancelButton.Focus();
        AnimateOpen();
    }

    private void PlaceOverOwner()
    {
        if (Owner is not { IsLoaded: true } owner)
        {
            var work = SystemParameters.WorkArea;
            Width = Math.Min(900, work.Width - 80);
            Height = Math.Min(780, work.Height - 80);
            Left = work.Left + ((work.Width - Width) / 2);
            Top = work.Top + ((work.Height - Height) / 2);
            return;
        }

        var anchor = owner.Content as FrameworkElement ?? owner;
        var origin = anchor.PointToScreen(new Point(0, 0));
        if (PresentationSource.FromVisual(anchor)?.CompositionTarget is { } target)
        {
            origin = target.TransformFromDevice.Transform(origin);
        }

        // The window covers the owner's client area, but never runs past the
        // screen: an owner that is itself taller than the work area would
        // otherwise centre the card off the bottom edge.
        var work2 = SystemParameters.WorkArea;
        var top = Math.Max(work2.Top, origin.Y);
        var bottom = Math.Min(work2.Bottom, origin.Y + anchor.ActualHeight);

        Left = origin.X;
        Top = top;
        Width = anchor.ActualWidth;
        Height = Math.Max(420, bottom - top - 8);
    }

    private void AnimateOpen()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Card.Opacity = 1;
            ScrimLayer.Opacity = 1;
            CardScale.ScaleX = 1;
            CardScale.ScaleY = 1;
            return;
        }

        Card.BeginAnimation(OpacityProperty, Spline(1, EnterMilliseconds));
        ScrimLayer.BeginAnimation(OpacityProperty, Spline(1, EnterMilliseconds));
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, Spline(1, EnterMilliseconds));
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, Spline(1, EnterMilliseconds));
    }

    /// <summary>The design guide's dialog curve, on the dialog's own 160 ms entry.</summary>
    private static DoubleAnimationUsingKeyFrames Spline(double to, int milliseconds)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var timeline = new DoubleAnimationUsingKeyFrames { Duration = duration };
        timeline.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(duration.TimeSpan),
            new KeySpline(0.22, 1, 0.36, 1)));
        return timeline;
    }

    /// <summary>
    /// The gates are display conditions on the same decision: acknowledging the
    /// change, and - when the action asks - typing the exact target. They can only
    /// withhold the confirmation, never grant it, and the caller's own gate is
    /// unchanged.
    /// </summary>
    private void UpdateConfirm()
    {
        var acknowledged = AckBox.IsChecked == true;
        var typed = !_model.RequiresTypedTarget
                    || string.Equals(
                        (ConfirmBox.Text ?? string.Empty).Trim(),
                        _model.ConfirmWord.Trim(),
                        StringComparison.OrdinalIgnoreCase);
        ConfirmButton.IsEnabled = acknowledged && typed;
        AutomationProperties.SetHelpText(
            ConfirmButton,
            acknowledged
                ? typed ? "Ready" : "Type the guest name to enable this action"
                : "Acknowledge the change to enable this action");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseAnimated();
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseAnimated();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CloseAnimated();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled || _closing)
        {
            return;
        }

        _closing = true;
        DialogResult = true;
    }

    /// <summary>Cancel fades out on the same curve and returns no DialogResult.</summary>
    private void CloseAnimated()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var fade = Spline(0, ExitMilliseconds);
        fade.Completed += (_, _) => Close();
        Card.BeginAnimation(OpacityProperty, fade);
        ScrimLayer.BeginAnimation(OpacityProperty, Spline(0, ExitMilliseconds));
    }
}
