using System.Windows;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// A shell window whose modal overlays (the command palette) cover the page
/// below its title bar instead of the whole client area. The mockups keep the
/// title bar legible while the palette's scrim dims the studio, and the palette
/// already sizes itself to its anchor rather than to the window.
/// </summary>
public interface IOverlayAnchor
{
    /// <summary>The element a modal overlay lines up with: the shell body.</summary>
    FrameworkElement OverlayAnchor { get; }
}
