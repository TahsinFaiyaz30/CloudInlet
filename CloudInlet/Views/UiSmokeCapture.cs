using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CloudInlet.Views;

public static class UiSmokeCapture
{
    public static async Task SaveAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        element.UpdateLayout();
        var scale = element.XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Ceiling(element.ActualWidth * scale);
        var height = (int)Math.Ceiling(element.ActualHeight * scale);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("The capture target has no visible bounds.");
        // Explicit pixel bounds prevent the render surface from retaining a
        // previous, larger window size after a DPI/resize transition.
        var previousClip = element.Clip;
        try
        {
            // A screenshot is the visible viewport. Explicitly clip the root so
            // offscreen expander/dialog descendants cannot enlarge its render
            // bounds and scale the visible page into one corner of the bitmap.
            element.Clip = new RectangleGeometry { Rect = new Rect(0, 0, element.ActualWidth, element.ActualHeight) };
            await bitmap.RenderAsync(element, width, height);
        }
        finally { element.Clip = previousClip; }
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            throw new InvalidOperationException("The UI is not laid out, so its smoke capture cannot be rendered.");
        var pixels = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(pixels);
        var data = new byte[pixels.Length];
        reader.ReadBytes(data);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, data);
        await encoder.FlushAsync();
        await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "capture-metrics.txt"),
            $"{Path.GetFileName(path)}: logical={element.ActualWidth:0.##}x{element.ActualHeight:0.##}, scale={scale:0.##}, pixels={bitmap.PixelWidth}x{bitmap.PixelHeight}{Environment.NewLine}");
    }
}
