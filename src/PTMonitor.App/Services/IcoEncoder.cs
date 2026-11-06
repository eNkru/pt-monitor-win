using System.Buffers.Binary;

namespace PTMonitor.App.Services;

/// <summary>
/// Writes a Windows icon container (".ico") in memory.
///
/// The tray icon is drawn at runtime, so the container is built here instead of
/// being committed as a binary asset. The layout is the classic one every
/// Windows tool writes:
///
/// <code>
/// ICONDIR            reserved = 0, type = 1 (icon), count
/// ICONDIRENTRY[n]    width, height, colour count, reserved, planes, bit count,
///                    size in bytes, offset
///   entry payload    BITMAPINFOHEADER + bottom-up BGRA pixels + AND mask
/// </code>
///
/// Every entry is a 32-bit BGRA image (BI_RGB) whose alpha channel does the
/// transparency; the AND mask that follows the pixels is all zero, exactly
/// like the icons Windows 11 ships. The alpha is *straight* (not
/// premultiplied) — a premultiplied copy would darken every antialiased edge.
/// </summary>
internal static class IcoEncoder
{
    private const int DirectorySize = 6;
    private const int EntrySize = 16;
    private const int DibHeaderSize = 40;

    /// <summary>Encodes the given images into one .ico container.</summary>
    /// <param name="images">One entry per icon size; pixels are BGRA, straight alpha.</param>
    public static byte[] Encode(IReadOnlyList<(int Size, byte[] Bgra)> images)
    {
        if (images.Count == 0)
            throw new ArgumentException("An icon needs at least one image.", nameof(images));

        var payloads = new byte[images.Count][];
        for (var i = 0; i < images.Count; i++)
        {
            var (size, bgra) = images[i];
            if (size <= 0)
                throw new ArgumentException("Icon sizes must be positive.", nameof(images));
            if (bgra.Length != size * size * 4)
                throw new ArgumentException(
                    $"The {size}px icon needs {size * size * 4} bytes of BGRA pixels, got {bgra.Length}.", nameof(images));
            payloads[i] = BuildDib(size, bgra);
        }

        var total = DirectorySize + EntrySize * images.Count;
        foreach (var payload in payloads) total += payload.Length;

        var bytes = new byte[total];
        var offset = 0;

        // ICONDIR
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), 0); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), 1); offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)images.Count); offset += 2;

        // ICONDIRENTRY, one per image; the payloads follow the whole table.
        var imageOffset = DirectorySize + EntrySize * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var size = images[i].Size;
            // 256 does not fit in a byte; 0 means 256.
            var dimension = size >= 256 ? (byte)0 : (byte)size;
            bytes[offset++] = dimension;
            bytes[offset++] = dimension;
            bytes[offset++] = 0;  // colour count: 0 means "32 bits per pixel"
            bytes[offset++] = 0;  // reserved
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), 1); offset += 2;   // planes
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), 32); offset += 2;  // bit count
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)payloads[i].Length); offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)imageOffset); offset += 4;
            imageOffset += payloads[i].Length;
        }

        foreach (var payload in payloads)
        {
            payload.CopyTo(bytes.AsSpan(offset));
            offset += payload.Length;
        }

        return bytes;
    }

    /// <summary>One icon entry: BITMAPINFOHEADER + pixels (bottom-up) + AND mask.</summary>
    private static byte[] BuildDib(int size, byte[] bgra)
    {
        var rowBytes = size * 4;
        // The AND mask is 1 bit per pixel with rows padded to 4 bytes. It stays
        // empty: the alpha channel carries the transparency.
        var maskStride = ((size + 31) / 32) * 4;
        var maskLength = maskStride * size;
        var dib = new byte[DibHeaderSize + rowBytes * size + maskLength];

        // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), DibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), size);
        // The height covers the colour bitmap *and* the mask.
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), size * 2);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);    // planes
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);   // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), 0);   // compression: BI_RGB
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), (uint)(rowBytes * size + maskLength));
        // Pixels per metre and the colour counts stay zero.

        // DIB rows run bottom-up, the other way round from our top-down pixels.
        for (var y = 0; y < size; y++)
        {
            var source = (size - 1 - y) * rowBytes;
            var target = DibHeaderSize + y * rowBytes;
            bgra.AsSpan(source, rowBytes).CopyTo(dib.AsSpan(target, rowBytes));
        }

        return dib;
    }
}
