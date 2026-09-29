using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TrayFolder.Services;

/// <summary>
/// Thumbnails straight from the shell (IShellItemImageFactory), the same images Explorer shows.
/// StorageFile.GetThumbnailAsync hands back a BMP whose alpha BitmapImage ignores, so icons and
/// transparent pictures get black backgrounds; this keeps the alpha.
/// </summary>
internal static partial class ShellThumbnail
{
    private const int SIIGBF_RESIZETOFIT = 0x0;
    private const uint DIB_RGB_COLORS = 0;

    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>Premultiplied BGRA pixels, top-down.</summary>
    public sealed record Pixels(byte[] Data, int Width, int Height);

    /// <summary>Reads the thumbnail (or icon) for <paramref name="path"/>. Call on a worker thread.</summary>
    /// <remarks>
    /// Calls the COM interface through its vtable rather than a [ComImport] interface: trimmed
    /// Release builds drop the runtime's built-in COM marshalling, which fails there at runtime.
    /// </remarks>
    public static unsafe Pixels? Load(string path, int size)
    {
        nint factory = 0;
        nint bitmap = 0;
        try
        {
            Guid id = ImageFactoryId;
            if (SHCreateItemFromParsingName(path, 0, in id, out factory) != 0 || factory == 0)
            {
                return null;
            }

            // IShellItemImageFactory: IUnknown's three slots, then GetImage(SIZE, SIIGBF, HBITMAP*).
            void** vtable = *(void***)factory;
            var getImage = (delegate* unmanaged[Stdcall]<nint, NativeSize, int, nint*, int>)vtable[3];
            if (getImage(factory, new NativeSize { Width = size, Height = size }, SIIGBF_RESIZETOFIT, &bitmap) != 0 || bitmap == 0)
            {
                return null;
            }

            return ReadBitmap(bitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (factory != 0)
            {
                Marshal.Release(factory);
            }
        }
    }

    /// <summary>Wraps pixels in an image source. UI thread only.</summary>
    public static WriteableBitmap ToImage(Pixels pixels)
    {
        WriteableBitmap image = new(pixels.Width, pixels.Height);
        using (Stream stream = image.PixelBuffer.AsStream())
        {
            stream.Write(pixels.Data, 0, pixels.Data.Length);
        }

        image.Invalidate();
        return image;
    }

    private static unsafe Pixels? ReadBitmap(nint bitmap)
    {
        NativeBitmap info;
        if (GetObject(bitmap, sizeof(NativeBitmap), &info) == 0 || info.Width <= 0 || info.Height == 0)
        {
            return null;
        }

        int width = info.Width;
        int height = Math.Abs(info.Height);
        byte[] data = new byte[width * height * 4];

        BitmapInfoHeader header = new()
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // negative: top-down rows
            Planes = 1,
            BitCount = 32,
        };

        nint dc = GetDC(0);
        try
        {
            fixed (byte* bits = data)
            {
                if (GetDIBits(dc, bitmap, 0, (uint)height, bits, &header, DIB_RGB_COLORS) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            ReleaseDC(0, dc);
        }

        // Pictures without transparency come back with alpha 0 everywhere; make them opaque.
        bool anyAlpha = false;
        for (int i = 3; i < data.Length; i += 4)
        {
            if (data[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }

        if (!anyAlpha)
        {
            for (int i = 3; i < data.Length; i += 4)
            {
                data[i] = 255;
            }
        }

        return new Pixels(data, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;

        // BITMAPINFO's single RGBQUAD, unused for 32bpp BI_RGB but part of the struct.
        public uint Colors;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid, out nint factory);

    [DllImport("gdi32.dll")]
    private static extern unsafe int GetObject(nint handle, int size, void* buffer);

    [DllImport("gdi32.dll")]
    private static extern unsafe int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
}
