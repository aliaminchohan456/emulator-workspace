using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-10: the viewport's one bitmap. It is allocated on the UI thread (a
/// <see cref="WriteableBitmap"/> has dispatcher affinity) and reused for every
/// frame of the same size; the image's Source keeps pointing at it until the key
/// changes, which is when a new surface is allocated.
/// </summary>
internal sealed class ViewportSurfaceCache
{
    private WriteableBitmap? _bitmap;
    private ViewportSurfaceKey? _key;

    /// <summary>The key of the surface currently held, for the report and tests.</summary>
    public ViewportSurfaceKey? Current => _key;

    public WriteableBitmap Get(int width, int height)
    {
        var key = ViewportSurfacePolicy.For(width, height);
        if (_bitmap is null || ViewportSurfacePolicy.Decide(_key, key) == ViewportSurfaceAction.Reallocate)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _key = key;
        }

        return _bitmap;
    }
}
