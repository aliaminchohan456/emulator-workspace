using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-10 / EV-10b: the viewport's one bitmap. It is allocated on the UI thread
/// (a <see cref="WriteableBitmap"/> has dispatcher affinity) and reused for
/// every frame of the same size and pixel layout; the image's Source keeps
/// pointing at it until the key changes, which is when a new surface is
/// allocated. EV-10b adds the layout to the key so a source the UI thread can
/// copy without a shuffle gets its own surface.
/// </summary>
internal sealed class ViewportSurfaceCache
{
    private WriteableBitmap? _bitmap;
    private ViewportSurfaceKey? _key;

    /// <summary>The key of the surface currently held, for the report and tests.</summary>
    public ViewportSurfaceKey? Current => _key;

    public WriteableBitmap Get(int width, int height) =>
        Get(width, height, ViewportSurfacePolicy.TargetPixelFormat);

    public WriteableBitmap Get(int width, int height, string pixelFormat)
    {
        var key = ViewportSurfacePolicy.For(width, height, pixelFormat);
        if (_bitmap is null || ViewportSurfacePolicy.Decide(_key, key) == ViewportSurfaceAction.Reallocate)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, Resolve(pixelFormat), null);
            _key = key;
        }

        return _bitmap;
    }

    private static PixelFormat Resolve(string pixelFormat) =>
        pixelFormat switch
        {
            "Rgb24" => PixelFormats.Rgb24,
            "Bgr24" => PixelFormats.Bgr24,
            _ => PixelFormats.Bgra32,
        };
}
