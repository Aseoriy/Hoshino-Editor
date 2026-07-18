using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HoshinoEditor.Services;

public sealed record ImageAdjustments(double Exposure = 0, double Contrast = 0, double Saturation = 0, double Temperature = 0);
public sealed record ImagePixelBuffer(int Width, int Height, double DpiX, double DpiY, int Stride, byte[] Pixels);

public static class ImageEditService
{
    public static BitmapSource Load(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static ImagePixelBuffer CapturePixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new ImagePixelBuffer(converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY, stride, pixels);
    }

    public static byte[] AdjustPixels(ImagePixelBuffer source, ImageAdjustments value)
    {
        var pixels = (byte[])source.Pixels.Clone();

        var exposure = Math.Pow(2, value.Exposure / 100.0);
        var contrast = 1 + value.Contrast / 100.0;
        var saturation = 1 + value.Saturation / 100.0;
        var warmth = value.Temperature * 0.32;

        Parallel.For(0, source.Height, y =>
        {
            var start = y * source.Stride;
            var end = start + source.Stride;
            for (var i = start; i < end; i += 4)
            {
                var b = pixels[i] * exposure - warmth;
                var g = pixels[i + 1] * exposure;
                var r = pixels[i + 2] * exposure + warmth;
                r = (r - 127.5) * contrast + 127.5;
                g = (g - 127.5) * contrast + 127.5;
                b = (b - 127.5) * contrast + 127.5;
                var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = luminance + (r - luminance) * saturation;
                g = luminance + (g - luminance) * saturation;
                b = luminance + (b - luminance) * saturation;
                pixels[i] = Clamp(b);
                pixels[i + 1] = Clamp(g);
                pixels[i + 2] = Clamp(r);
            }
        });

        return pixels;
    }

    public static BitmapSource CreateBitmap(ImagePixelBuffer source, byte[] pixels)
    {
        var result = BitmapSource.Create(source.Width, source.Height, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, source.Stride);
        result.Freeze();
        return result;
    }

    public static BitmapSource Rotate(BitmapSource source, double angle)
    {
        var transformed = new TransformedBitmap(source, new RotateTransform(angle));
        transformed.Freeze();
        return transformed;
    }

    public static BitmapSource Flip(BitmapSource source, bool horizontal)
    {
        var transformed = new TransformedBitmap(source, horizontal ? new ScaleTransform(-1, 1) : new ScaleTransform(1, -1));
        transformed.Freeze();
        return transformed;
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, source.PixelWidth - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, source.PixelHeight - 1));
        rect = new Int32Rect(x, y, Math.Clamp(rect.Width, 0, source.PixelWidth - x), Math.Clamp(rect.Height, 0, source.PixelHeight - y));
        if (rect.Width < 2 || rect.Height < 2) return source;
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    public static BitmapSource Resize(BitmapSource source, int width, int height, bool highQuality = true)
    {
        width = Math.Clamp(width, 1, 32768);
        height = Math.Clamp(height, 1, 32768);
        var pixels = (long)width * height;
        if (pixels > 120_000_000) throw new InvalidOperationException("That size would exceed the 120 megapixel safety limit.");

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, highQuality ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect(0, 0, width, height));
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    public static BitmapSource RemoveBackground(BitmapSource source, double tolerance, double feather)
    {
        var buffer = CapturePixels(source);
        var pixels = (byte[])buffer.Pixels.Clone();
        var samples = new List<(double B, double G, double R)>();
        var sampleSize = Math.Max(1, Math.Min(buffer.Width, buffer.Height) / 25);
        foreach (var (sx, sy) in new[] { (0, 0), (buffer.Width - sampleSize, 0), (0, buffer.Height - sampleSize), (buffer.Width - sampleSize, buffer.Height - sampleSize) })
        {
            for (var y = sy; y < Math.Min(buffer.Height, sy + sampleSize); y += Math.Max(1, sampleSize / 4))
            for (var x = sx; x < Math.Min(buffer.Width, sx + sampleSize); x += Math.Max(1, sampleSize / 4))
            {
                var i = y * buffer.Stride + x * 4;
                samples.Add((pixels[i], pixels[i + 1], pixels[i + 2]));
            }
        }
        var bgB = samples.Average(c => c.B); var bgG = samples.Average(c => c.G); var bgR = samples.Average(c => c.R);
        tolerance = Math.Clamp(tolerance, 0, 220);
        feather = Math.Clamp(feather, 1, 180);

        Parallel.For(0, buffer.Height, y =>
        {
            for (var x = 0; x < buffer.Width; x++)
            {
                var i = y * buffer.Stride + x * 4;
                var db = pixels[i] - bgB; var dg = pixels[i + 1] - bgG; var dr = pixels[i + 2] - bgR;
                var distance = Math.Sqrt(db * db * .72 + dg * dg + dr * dr * .86);
                var alpha = Math.Clamp((distance - tolerance) / feather, 0, 1);
                alpha = alpha * alpha * (3 - 2 * alpha);
                pixels[i + 3] = (byte)Math.Round(pixels[i + 3] * alpha);
            }
        });
        return CreateBitmap(buffer, pixels);
    }

    public static void Save(BitmapSource source, string path, int jpegQuality = 92)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
