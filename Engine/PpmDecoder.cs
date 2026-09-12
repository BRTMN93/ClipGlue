using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipGlue.Engine;

/// <summary>
/// Decodes a binary PPM (P6) image - the format the trimmed ffmpeg-mini
/// build's "ppm" encoder produces for a single extracted preview frame
/// (see FfmpegEngine.ExtractFramePpm). Tkinter's tk.PhotoImage(data=...)
/// reads PPM natively; WPF has no built-in PPM decoder, so this is a small
/// hand-rolled replacement for that one call.
/// </summary>
public static class PpmDecoder
{
    public static BitmapSource? Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);

        string? ReadToken()
        {
            int c;
            // Skip whitespace and '#' comment lines between header fields.
            while (true)
            {
                c = ms.ReadByte();
                if (c == -1) return null;
                if (c == '#')
                {
                    do { c = ms.ReadByte(); } while (c != -1 && c != '\n');
                    continue;
                }
                if (!char.IsWhiteSpace((char)c)) break;
            }
            var sb = new StringBuilder();
            sb.Append((char)c);
            while (true)
            {
                c = ms.ReadByte();
                if (c == -1 || char.IsWhiteSpace((char)c)) break;
                sb.Append((char)c);
            }
            return sb.ToString();
        }

        if (ReadToken() != "P6") return null;
        if (!int.TryParse(ReadToken(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)) return null;
        if (!int.TryParse(ReadToken(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)) return null;
        if (ReadToken() is null) return null; // maxval - ffmpeg's ppm encoder always writes 255 (one byte/channel)
        if (width <= 0 || height <= 0) return null;

        int pixelBytes = width * height * 3;
        var pixels = new byte[pixelBytes];
        int offset = 0;
        while (offset < pixelBytes)
        {
            int read = ms.Read(pixels, offset, pixelBytes - offset);
            if (read <= 0) return null;
            offset += read;
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        bmp.Freeze();
        return bmp;
    }
}
