using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NetTrans.Diagnostics;

/// <summary>
/// 把每一屏都拍下来.
///
/// The handoff specifies about twenty screens. Until this existed exactly one
/// of them had ever been looked at -- the task list with the seed data in it --
/// and the conclusion drawn from that one screen was reported as if it covered
/// the app. The first thing anybody actually sees, an empty queue, had never
/// been rendered by anyone.
///
/// So: the app walks its own states and renders each one to a PNG. Nothing here
/// asserts anything; the point is that every screen exists as an image that can
/// be put next to the design and argued about.
///
/// RenderTargetBitmap rather than a desktop grab: it renders the element's own
/// visual tree, so a console window sitting on top of the app on a CI runner
/// cannot get into the picture, and each frame comes out at exactly its design
/// size regardless of where the window happens to be.
/// </summary>
public sealed class ScreenWalk
{
    private readonly string _directory;
    private int _index;

    public ScreenWalk(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Renders one state. The name becomes the file name, prefixed with the
    /// order it was taken in so a directory listing reads as the walk.
    /// </summary>
    public async Task CaptureAsync(string name, UIElement element, int width, int height)
    {
        // Two frames of settle: the sheets animate in over .34s and a bitmap
        // taken mid-animation shows a form halfway up the window.
        await Task.Delay(600);

        string path = Path.Combine(_directory, $"{++_index:00}-{name}.png");

        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element, width, height);

            // IBuffer, not a byte[]: LINQ's ToArray is not the one you want here,
            // and picking it up by accident is a compile error, not a bug.
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var source = DataReader.FromBuffer(buffer)) source.ReadBytes(pixels);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                pixels);
            await encoder.FlushAsync();

            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            File.WriteAllBytes(path, bytes);
            Startup.Log($"拍下 {Path.GetFileName(path)}  {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        }
        catch (Exception exception)
        {
            // One screen that will not render must not cost the other twenty.
            Startup.Log($"拍 {name} 失败：{exception.GetType().Name}: {exception.Message}");
        }
    }
}
