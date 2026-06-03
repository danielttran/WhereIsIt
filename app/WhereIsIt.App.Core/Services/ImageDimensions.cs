using System;
using System.IO;

namespace WhereIsIt.App.Services;

/// <summary>
/// Minimal, dependency-free image dimension reader for the <c>width:</c>,
/// <c>height:</c> and <c>dimensions:</c> search functions. Parses just the
/// header of PNG / JPEG / GIF / BMP / WEBP files — enough to recover pixel
/// width and height without decoding the image. Returns false for anything it
/// doesn't recognise, so non-image rows simply don't match those filters.
/// </summary>
public static class ImageDimensions
{
    // Read a bounded header window; SOF markers in JPEG are normally well within this.
    private const int HeaderBytes = 64 * 1024;

    public static bool TryRead(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int len = (int)Math.Min(fs.Length, HeaderBytes);
            if (len < 12) return false;
            var b = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(b, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < 12) return false;
            return TryParse(b, read, out width, out height);
        }
        catch { return false; }
    }

    /// <summary>Reads the EXIF orientation (1–8) from a JPEG, for the
    /// <c>orientation:</c> search function. False when absent/unreadable.</summary>
    public static bool TryReadOrientation(string path, out int orientation)
    {
        orientation = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int len = (int)Math.Min(fs.Length, HeaderBytes);
            if (len < 12) return false;
            var b = new byte[len];
            int read = 0;
            while (read < len) { int n = fs.Read(b, read, len - read); if (n <= 0) break; read += n; }
            return ParseExifOrientation(b, read, out orientation);
        }
        catch { return false; }
    }

    /// <summary>Parses EXIF orientation from an in-memory JPEG buffer. Exposed for tests.</summary>
    public static bool ParseExifOrientation(byte[] b, int len, out int orientation)
    {
        orientation = 0;
        if (len < 4 || b[0] != 0xFF || b[1] != 0xD8) return false;
        int p = 2;
        while (p + 4 <= len)
        {
            if (b[p] != 0xFF) { p++; continue; }
            byte marker = b[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker is 0xD8 or 0xD9 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
            int segLen = Be16(b, p + 2);
            if (segLen < 2) return false;
            // APP1 with an "Exif\0\0" header carries the TIFF/IFD with orientation.
            if (marker == 0xE1 && p + 10 <= len
                && b[p + 4] == (byte)'E' && b[p + 5] == (byte)'x' && b[p + 6] == (byte)'i' && b[p + 7] == (byte)'f'
                && b[p + 8] == 0 && b[p + 9] == 0)
            {
                int tiff = p + 10;
                if (tiff + 8 > len) return false;
                bool le = b[tiff] == 0x49 && b[tiff + 1] == 0x49;
                bool be = b[tiff] == 0x4D && b[tiff + 1] == 0x4D;
                if (!le && !be) return false;
                int ifd = tiff + ReadU32(b, tiff + 4, le);
                if (ifd + 2 > len) return false;
                int count = ReadU16(b, ifd, le);
                int e = ifd + 2;
                for (int k = 0; k < count && e + 12 <= len; k++, e += 12)
                {
                    if (ReadU16(b, e, le) == 0x0112)
                    {
                        orientation = ReadU16(b, e + 8, le);
                        return orientation is >= 1 and <= 8;
                    }
                }
                return false;
            }
            p += 2 + segLen;
        }
        return false;
    }

    private static int ReadU16(byte[] b, int i, bool le) => le ? (b[i] | (b[i + 1] << 8)) : ((b[i] << 8) | b[i + 1]);
    private static int ReadU32(byte[] b, int i, bool le) => le
        ? (b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24))
        : ((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);

    /// <summary>Parses dimensions from an in-memory header buffer. Exposed for tests.</summary>
    public static bool TryParse(byte[] b, int len, out int width, out int height)
    {
        width = height = 0;

        // PNG: 8-byte signature, then IHDR with width@16, height@20 (big-endian).
        if (len >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            width = Be32(b, 16); height = Be32(b, 20);
            return width > 0 && height > 0;
        }

        // GIF: "GIF", logical screen width@6, height@8 (little-endian).
        if (len >= 10 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
        {
            width = Le16(b, 6); height = Le16(b, 8);
            return width > 0 && height > 0;
        }

        // BMP: "BM", width@18, height@22 (little-endian; height may be negative).
        if (len >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M')
        {
            width = Le32(b, 18); height = Math.Abs(Le32(b, 22));
            return width > 0 && height > 0;
        }

        // WEBP: "RIFF"...."WEBP" then a VP8/VP8L/VP8X chunk.
        if (len >= 30 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
        {
            if (TryWebp(b, len, out width, out height)) return true;
        }

        // JPEG: scan segments for a Start-Of-Frame marker.
        if (len >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            return TryJpeg(b, len, out width, out height);

        return false;
    }

    private static bool TryJpeg(byte[] b, int len, out int width, out int height)
    {
        width = height = 0;
        int p = 2;
        while (p + 9 < len)
        {
            if (b[p] != 0xFF) { p++; continue; }
            byte marker = b[p + 1];
            // Standalone markers (no length): padding 0xFF, RSTn, SOI/EOI.
            if (marker == 0xFF) { p++; continue; }
            if (marker is 0xD8 or 0xD9 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }

            int segLen = Be16(b, p + 2);
            if (segLen < 2) return false;
            // SOF markers carry the frame dimensions (exclude DHT/JPG/DAC/DRI).
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof && p + 9 < len)
            {
                height = Be16(b, p + 5);
                width = Be16(b, p + 7);
                return width > 0 && height > 0;
            }
            p += 2 + segLen;
        }
        return false;
    }

    private static bool TryWebp(byte[] b, int len, out int width, out int height)
    {
        width = height = 0;
        // Chunk FourCC at offset 12.
        if (len < 30) return false;
        // VP8X (extended): 24-bit canvas width-1 @24, height-1 @27 (little-endian).
        if (b[12] == (byte)'V' && b[13] == (byte)'P' && b[14] == (byte)'8' && b[15] == (byte)'X')
        {
            width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
            height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
            return width > 0 && height > 0;
        }
        // VP8L (lossless): dimensions packed in 14 bits each after the 0x2F sig.
        if (b[12] == (byte)'V' && b[13] == (byte)'P' && b[14] == (byte)'8' && b[15] == (byte)'L'
            && len >= 25 && b[20] == 0x2F)
        {
            int bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
            width = (bits & 0x3FFF) + 1;
            height = ((bits >> 14) & 0x3FFF) + 1;
            return width > 0 && height > 0;
        }
        // VP8 (lossy): 16-bit width@26 and height@28 (14 significant bits).
        if (b[12] == (byte)'V' && b[13] == (byte)'P' && b[14] == (byte)'8' && b[15] == (byte)' '
            && len >= 30)
        {
            width = Le16(b, 26) & 0x3FFF;
            height = Le16(b, 28) & 0x3FFF;
            return width > 0 && height > 0;
        }
        return false;
    }

    private static int Be32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static int Be16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
    private static int Le16(byte[] b, int i) => b[i] | (b[i + 1] << 8);
    private static int Le32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
}
