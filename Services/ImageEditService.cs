using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Globalization;

namespace HoshinoEditor.Services;

public sealed record ImageAdjustments(double Exposure = 0, double Contrast = 0, double Saturation = 0, double Temperature = 0,
    double Highlights = 0, double Shadows = 0, double Vibrance = 0);
public sealed record ImagePixelBuffer(int Width, int Height, double DpiX, double DpiY, int Stride, byte[] Pixels);
public enum BackgroundRemovalMode { OuterOnly, InnerOnly, AllMatching }

public static class ImageEditService
{
    private const long MaxImagePixels = 50_000_000;

    public static BitmapSource FreezeForBackgroundAccess(BitmapSource source)
    {
        if (source.IsFrozen) return source;
        var clone = source.CloneCurrentValue();
        clone.Freeze();
        return clone;
    }

    public static BitmapSource Load(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var headerDecoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var header = headerDecoder.Frames[0];
        ValidateImageDimensions(header.PixelWidth, header.PixelHeight);
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static BitmapSource CreatePreview(BitmapSource source, int maxDimension = 2048, bool highQuality = true)
    {
        maxDimension = Math.Clamp(maxDimension, 64, 8192);
        var largestDimension = Math.Max(source.PixelWidth, source.PixelHeight);
        if (largestDimension <= maxDimension) return source;
        var scale = maxDimension / (double)largestDimension;
        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        return Resize(source, width, height, highQuality);
    }

    public static ImagePixelBuffer CapturePixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        ValidateImageDimensions(converted.PixelWidth, converted.PixelHeight);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return new ImagePixelBuffer(converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY, stride, pixels);
    }

    public static byte[] AdjustPixels(ImagePixelBuffer source, ImageAdjustments value, CancellationToken cancellationToken = default)
    {
        var pixels = (byte[])source.Pixels.Clone();

        var exposure = Math.Pow(2, value.Exposure / 100.0);
        var contrast = 1 + value.Contrast / 100.0;
        var saturation = 1 + value.Saturation / 100.0;
        var warmth = value.Temperature * 0.32;
        var highlights = value.Highlights / 100.0;
        var shadows = value.Shadows / 100.0;
        var vibrance = value.Vibrance / 100.0;

        Parallel.For(0, source.Height, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8))
        }, y =>
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
                var normalizedLuminance = Math.Clamp(luminance / 255.0, 0, 1);
                var tonalShift = 150 * (shadows * Math.Pow(1 - normalizedLuminance, 2) + highlights * Math.Pow(normalizedLuminance, 2));
                r += tonalShift; g += tonalShift; b += tonalShift;
                luminance += tonalShift;
                r = luminance + (r - luminance) * saturation;
                g = luminance + (g - luminance) * saturation;
                b = luminance + (b - luminance) * saturation;
                var maximum = Math.Max(r, Math.Max(g, b));
                var minimum = Math.Min(r, Math.Min(g, b));
                var colorfulness = Math.Clamp((maximum - minimum) / 255.0, 0, 1);
                var vibranceFactor = 1 + vibrance * (1 - colorfulness) * 1.35;
                r = luminance + (r - luminance) * vibranceFactor;
                g = luminance + (g - luminance) * vibranceFactor;
                b = luminance + (b - luminance) * vibranceFactor;
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

    public static BitmapSource CreateTextBitmap(string text, string fontFamily, double fontSize, bool bold, Color color)
    {
        text = string.IsNullOrWhiteSpace(text) ? "Text" : text.Trim();
        fontSize = Math.Clamp(fontSize, 8, 400);
        var typeface = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily),
            FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            fontSize, new SolidColorBrush(color), 1.0) { TextAlignment = TextAlignment.Left, MaxTextWidth = 4096 };
        var width = Math.Clamp((int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + 24), 2, 8192);
        var height = Math.Clamp((int)Math.Ceiling(formatted.Height + 20), 2, 8192);
        if ((long)width * height > MaxImagePixels) throw new InvalidOperationException("That text layer exceeds the 50 megapixel image safety limit.");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawText(formatted, new Point(12, 10));
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
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
        if (pixels > MaxImagePixels) throw new InvalidOperationException("That size would exceed the 50 megapixel safety limit.");

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, highQuality ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect(0, 0, width, height));
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    public static BitmapSource RemoveBackground(BitmapSource source, double tolerance, double feather,
        BackgroundRemovalMode mode = BackgroundRemovalMode.OuterOnly)
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

        // Mark pixels resembling the sampled corner color that are connected to
        // an image edge. This preserves enclosed same-color details in OuterOnly.
        var outer = mode == BackgroundRemovalMode.AllMatching ? null : FindEdgeConnectedBackground(
            buffer, pixels, bgB, bgG, bgR, tolerance + feather);

        Parallel.For(0, buffer.Height, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8))
        }, y =>
        {
            for (var x = 0; x < buffer.Width; x++)
            {
                var i = y * buffer.Stride + x * 4;
                var db = pixels[i] - bgB; var dg = pixels[i + 1] - bgG; var dr = pixels[i + 2] - bgR;
                var distance = Math.Sqrt(db * db * .72 + dg * dg + dr * dr * .86);
                var isOuter = outer is not null && outer[y * buffer.Width + x] != 0;
                var shouldRemove = mode switch
                {
                    BackgroundRemovalMode.OuterOnly => isOuter,
                    BackgroundRemovalMode.InnerOnly => !isOuter && distance <= tolerance + feather,
                    _ => distance <= tolerance + feather
                };
                if (!shouldRemove) continue;
                var alpha = Math.Clamp((distance - tolerance) / feather, 0, 1);
                alpha = alpha * alpha * (3 - 2 * alpha);
                pixels[i + 3] = (byte)Math.Round(pixels[i + 3] * alpha);
            }
        });
        return CreateBitmap(buffer, pixels);
    }

    private static byte[] FindEdgeConnectedBackground(ImagePixelBuffer buffer, byte[] pixels,
        double bgB, double bgG, double bgR, double threshold)
    {
        var width = buffer.Width; var height = buffer.Height;
        var connected = new byte[checked(width * height)];
        var queue = new Queue<int>(Math.Min(width * 2 + height * 2, 65_536));

        bool Matches(int x, int y)
        {
            var i = y * buffer.Stride + x * 4;
            var db = pixels[i] - bgB; var dg = pixels[i + 1] - bgG; var dr = pixels[i + 2] - bgR;
            return Math.Sqrt(db * db * .72 + dg * dg + dr * dr * .86) <= threshold;
        }
        void Enqueue(int x, int y)
        {
            var index = y * width + x;
            if (connected[index] != 0 || !Matches(x, y)) return;
            connected[index] = 1; queue.Enqueue(index);
        }

        for (var x = 0; x < width; x++) { Enqueue(x, 0); if (height > 1) Enqueue(x, height - 1); }
        for (var y = 1; y < height - 1; y++) { Enqueue(0, y); if (width > 1) Enqueue(width - 1, y); }
        while (queue.Count > 0)
        {
            var index = queue.Dequeue(); var x = index % width; var y = index / width;
            if (x > 0) Enqueue(x - 1, y); if (x + 1 < width) Enqueue(x + 1, y);
            if (y > 0) Enqueue(x, y - 1); if (y + 1 < height) Enqueue(x, y + 1);
        }
        return connected;
    }

    public static BitmapSource Grayscale(BitmapSource source)
    {
        var buffer = CapturePixels(source);
        var pixels = (byte[])buffer.Pixels.Clone();
        Parallel.For(0, buffer.Height, y =>
        {
            for (var i = y * buffer.Stride; i < (y + 1) * buffer.Stride; i += 4)
            {
                var luminance = Clamp(pixels[i + 2] * .2126 + pixels[i + 1] * .7152 + pixels[i] * .0722);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = luminance;
            }
        });
        return CreateBitmap(buffer, pixels);
    }

    public static BitmapSource Sepia(BitmapSource source)
    {
        var buffer = CapturePixels(source);
        var pixels = (byte[])buffer.Pixels.Clone();
        Parallel.For(0, buffer.Height, y =>
        {
            for (var i = y * buffer.Stride; i < (y + 1) * buffer.Stride; i += 4)
            {
                var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
                pixels[i + 2] = Clamp(r * .393 + g * .769 + b * .189);
                pixels[i + 1] = Clamp(r * .349 + g * .686 + b * .168);
                pixels[i] = Clamp(r * .272 + g * .534 + b * .131);
            }
        });
        return CreateBitmap(buffer, pixels);
    }

    public static BitmapSource AutoTone(BitmapSource source)
    {
        var buffer = CapturePixels(source);
        var pixels = (byte[])buffer.Pixels.Clone();
        var histograms = new[] { new int[256], new int[256], new int[256] };
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            histograms[0][pixels[i]]++; histograms[1][pixels[i + 1]]++; histograms[2][pixels[i + 2]]++;
        }
        var visiblePixels = histograms[0].Sum();
        if (visiblePixels == 0) return source;
        var lows = new[] { -1, -1, -1 }; var highs = new int[3];
        for (var channel = 0; channel < 3; channel++)
        {
            var lowTarget = visiblePixels * .005; var highTarget = visiblePixels * .995; var cumulative = 0;
            for (var value = 0; value < 256; value++)
            {
                cumulative += histograms[channel][value];
                if (cumulative >= lowTarget && lows[channel] < 0) lows[channel] = value;
                if (cumulative >= highTarget) { highs[channel] = value; break; }
            }
            if (lows[channel] < 0 || highs[channel] <= lows[channel]) { lows[channel] = 0; highs[channel] = 255; }
        }
        Parallel.For(0, buffer.Height, y =>
        {
            for (var i = y * buffer.Stride; i < (y + 1) * buffer.Stride; i += 4)
            for (var channel = 0; channel < 3; channel++)
                pixels[i + channel] = Clamp((pixels[i + channel] - lows[channel]) * 255d / (highs[channel] - lows[channel]));
        });
        return CreateBitmap(buffer, pixels);
    }

    public static BitmapSource Sharpen(BitmapSource source)
    {
        var buffer = CapturePixels(source);
        if (buffer.Width < 3 || buffer.Height < 3) return source;
        var result = (byte[])buffer.Pixels.Clone();
        Parallel.For(1, buffer.Height - 1, y =>
        {
            for (var x = 1; x < buffer.Width - 1; x++)
            {
                var index = y * buffer.Stride + x * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = buffer.Pixels[index + channel] * 5
                        - buffer.Pixels[index - 4 + channel] - buffer.Pixels[index + 4 + channel]
                        - buffer.Pixels[index - buffer.Stride + channel] - buffer.Pixels[index + buffer.Stride + channel];
                    result[index + channel] = Clamp(value);
                }
            }
        });
        return CreateBitmap(buffer, result);
    }

    public static void Save(BitmapSource source, string path, int jpegQuality = 92)
    {
        path = Path.GetFullPath(path);
        jpegQuality = Math.Clamp(jpegQuality, 1, 100);
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(source));
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Choose a valid image export folder.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static void ValidateImageDimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > 32_768 || height > 32_768 || (long)width * height > MaxImagePixels)
            throw new InvalidDataException("Images must be no larger than 32,768 pixels per side and 50 megapixels total.");
    }
}
