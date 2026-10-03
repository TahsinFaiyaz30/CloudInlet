using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CloudBay.Views;

public static class UiSmokeCapture
{
    public static async Task SaveAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
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
        var scale = element.XamlRoot?.RasterizationScale ?? 1;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, data);
        await encoder.FlushAsync();
    }
}
