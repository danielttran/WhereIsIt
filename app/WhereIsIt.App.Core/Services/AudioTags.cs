using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WhereIsIt.App.Services;

/// <summary>Recognised media-tag fields for the audio property search functions.</summary>
public enum MediaField { Title, Artist, Album, Year, Genre, Track, Comment }

/// <summary>A single media-property filter: a field plus a substring to match.</summary>
public sealed record MediaFilter(MediaField Field, string Value);

/// <summary>
/// Dependency-free ID3 tag reader for MP3 audio property search
/// (<c>artist:</c>, <c>album:</c>, <c>title:</c>, <c>year:</c>, <c>genre:</c>,
/// <c>track:</c>, <c>comment:</c>). Reads ID3v2.3/2.4 text frames, falling back
/// to a trailing ID3v1 tag. Other formats / containers return false (no match),
/// matching how a metadata-less row behaves.
/// </summary>
public sealed class AudioTags
{
    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    public string? Year { get; private set; }
    public string? Genre { get; private set; }
    public string? Track { get; private set; }
    public string? Comment { get; private set; }

    public string? Get(MediaField f) => f switch
    {
        MediaField.Title => Title,
        MediaField.Artist => Artist,
        MediaField.Album => Album,
        MediaField.Year => Year,
        MediaField.Genre => Genre,
        MediaField.Track => Track,
        MediaField.Comment => Comment,
        _ => null,
    };

    private bool Complete =>
        Title is not null && Artist is not null && Album is not null && Year is not null
        && Genre is not null && Track is not null && Comment is not null;

    /// <summary>True when the file's tags satisfy every media filter (each value
    /// matched as a substring of the corresponding tag). A file whose tags can't
    /// be read, or that lacks a filtered field, doesn't match.</summary>
    public static bool Match(IReadOnlyList<MediaFilter> filters, string path,
        bool caseSensitive, bool matchDiacritics)
    {
        if (!TryRead(path, out var tags)) return false;
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var mf in filters)
        {
            var val = tags.Get(mf.Field);
            if (val is null) return false;
            var hay = matchDiacritics ? val : QueryParser.RemoveDiacritics(val);
            var needle = matchDiacritics ? mf.Value : QueryParser.RemoveDiacritics(mf.Value);
            if (!hay.Contains(needle, cmp)) return false;
        }
        return true;
    }

    public static bool TryRead(string path, out AudioTags tags)
    {
        tags = new AudioTags();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[12];
            int hn = fs.Read(head, 0, head.Length);
            // FLAC carries Vorbis comments; M4A/MP4 carries iTunes-style atoms.
            if (hn >= 4 && head[0] == (byte)'f' && head[1] == (byte)'L'
                && head[2] == (byte)'a' && head[3] == (byte)'C')
            {
                fs.Seek(4, SeekOrigin.Begin);
                return tags.ReadFlac(fs);
            }
            if (hn >= 8 && head[4] == (byte)'f' && head[5] == (byte)'t'
                && head[6] == (byte)'y' && head[7] == (byte)'p')
                return tags.ReadM4a(fs);

            bool any = tags.ReadId3v2(fs); // re-seeks to 0 itself
            if (!tags.Complete) any |= tags.ReadId3v1(fs);
            return any;
        }
        catch { return false; }
    }

    // ── FLAC (Vorbis comment metadata block) ─────────────────────────────

    private bool ReadFlac(FileStream fs)
    {
        // Positioned just past the 4-byte "fLaC" marker.
        bool any = false;
        for (int guard = 0; guard < 128; guard++)
        {
            var bh = new byte[4];
            if (fs.Read(bh, 0, 4) != 4) break;
            bool last = (bh[0] & 0x80) != 0;
            int type = bh[0] & 0x7F;
            int len = (bh[1] << 16) | (bh[2] << 8) | bh[3];
            if (len < 0 || len > 8 * 1024 * 1024) break;

            if (type == 4) // VORBIS_COMMENT
            {
                var block = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(block, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                any |= ParseVorbisComment(block, read);
                break; // comments found; no need to scan further
            }

            if (last) break;
            fs.Seek(len, SeekOrigin.Current); // skip non-comment block
        }
        return any;
    }

    private bool ParseVorbisComment(byte[] b, int len)
    {
        int p = 0;
        if (p + 4 > len) return false;
        int vendorLen = Le32(b, p); p += 4;
        p += vendorLen;
        if (p + 4 > len) return false;
        int count = Le32(b, p); p += 4;
        bool any = false;
        for (int i = 0; i < count && p + 4 <= len; i++)
        {
            int clen = Le32(b, p); p += 4;
            if (clen < 0 || p + clen > len) break;
            var comment = Encoding.UTF8.GetString(b, p, clen);
            p += clen;
            int eq = comment.IndexOf('=');
            if (eq <= 0) continue;
            var field = MapVorbis(comment[..eq]);
            if (field is not null) { Assign(field.Value, comment[(eq + 1)..]); any = true; }
        }
        return any;
    }

    private static MediaField? MapVorbis(string key) => key.ToUpperInvariant() switch
    {
        "TITLE" => MediaField.Title,
        "ARTIST" or "ALBUMARTIST" => MediaField.Artist,
        "ALBUM" => MediaField.Album,
        "DATE" or "YEAR" => MediaField.Year,
        "GENRE" => MediaField.Genre,
        "TRACKNUMBER" or "TRACK" => MediaField.Track,
        "COMMENT" or "DESCRIPTION" => MediaField.Comment,
        _ => null,
    };

    private static int Le32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);

    // ── M4A / MP4 (iTunes-style metadata atoms) ──────────────────────────

    private bool ReadM4a(FileStream fs)
    {
        long end = fs.Length;
        if (!FindAtom(fs, 0, end, "moov", out long ms, out long me)) return false;
        if (!FindAtom(fs, ms, me, "udta", out long us, out long ue)) return false;
        if (!FindAtom(fs, us, ue, "meta", out long mes, out long mee)) return false;
        // The meta atom carries a 4-byte version/flags before its children.
        if (!FindAtom(fs, mes + 4, mee, "ilst", out long ls, out long le)) return false;
        return ParseIlst(fs, ls, le);
    }

    private bool ParseIlst(FileStream fs, long start, long end)
    {
        bool any = false;
        long pos = start;
        var hdr = new byte[8];
        while (pos + 8 <= end)
        {
            fs.Seek(pos, SeekOrigin.Begin);
            if (fs.Read(hdr, 0, 8) != 8) break;
            long size = ReadAtomSize(hdr);
            if (size < 8 || pos + size > end) break;

            var field = MapM4a(hdr[4], hdr[5], hdr[6], hdr[7]);
            if (field is not null && FindAtom(fs, pos + 8, pos + size, "data", out long ds, out long de))
            {
                // data payload = 4-byte type + 4-byte locale, then the value.
                long vstart = ds + 8;
                int vlen = (int)(de - vstart);
                if (vlen > 0 && vlen < 1024 * 1024)
                {
                    var vb = new byte[vlen];
                    fs.Seek(vstart, SeekOrigin.Begin);
                    int r = 0; while (r < vlen) { int n = fs.Read(vb, r, vlen - r); if (n <= 0) break; r += n; }
                    string val = field == MediaField.Track && r >= 4
                        ? ((vb[2] << 8) | vb[3]).ToString()
                        : Encoding.UTF8.GetString(vb, 0, r).Trim('\0').Trim();
                    if (val.Length > 0) { Assign(field.Value, val); any = true; }
                }
            }
            pos += size;
        }
        return any;
    }

    private static MediaField? MapM4a(byte a, byte b, byte c, byte d)
    {
        const byte C = 0xA9; // the © marker prefixing iTunes text atoms
        if (a == C && b == (byte)'A' && c == (byte)'R' && d == (byte)'T') return MediaField.Artist;
        if (a == (byte)'a' && b == (byte)'A' && c == (byte)'R' && d == (byte)'T') return MediaField.Artist; // album artist
        if (a == C && b == (byte)'a' && c == (byte)'l' && d == (byte)'b') return MediaField.Album;
        if (a == C && b == (byte)'n' && c == (byte)'a' && d == (byte)'m') return MediaField.Title;
        if (a == C && b == (byte)'d' && c == (byte)'a' && d == (byte)'y') return MediaField.Year;
        if (a == C && b == (byte)'g' && c == (byte)'e' && d == (byte)'n') return MediaField.Genre;
        if (a == C && b == (byte)'c' && c == (byte)'m' && d == (byte)'t') return MediaField.Comment;
        if (a == (byte)'t' && b == (byte)'r' && c == (byte)'k' && d == (byte)'n') return MediaField.Track;
        return null;
    }

    /// <summary>Finds a direct child atom of the given 4-char type within
    /// [start,end); returns its content range (excluding the atom header).</summary>
    private static bool FindAtom(FileStream fs, long start, long end, string type,
        out long contentStart, out long contentEnd)
    {
        contentStart = contentEnd = 0;
        long pos = start;
        var hdr = new byte[8];
        while (pos + 8 <= end)
        {
            fs.Seek(pos, SeekOrigin.Begin);
            if (fs.Read(hdr, 0, 8) != 8) break;
            long size = (uint)((hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3]);
            int headerLen = 8;
            if (size == 1)
            {
                var ext = new byte[8];
                if (fs.Read(ext, 0, 8) != 8) break;
                size = 0;
                for (int i = 0; i < 8; i++) size = (size << 8) | ext[i];
                headerLen = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < headerLen || pos + size > end) break;

            if (hdr[4] == type[0] && hdr[5] == type[1] && hdr[6] == type[2] && hdr[7] == type[3])
            {
                contentStart = pos + headerLen;
                contentEnd = pos + size;
                return true;
            }
            pos += size;
        }
        return false;
    }

    private static long ReadAtomSize(byte[] hdr)
        => (uint)((hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3]);

    // ── ID3v2 (header at start of file) ──────────────────────────────────

    private bool ReadId3v2(FileStream fs)
    {
        fs.Seek(0, SeekOrigin.Begin);
        var hdr = new byte[10];
        if (fs.Read(hdr, 0, 10) != 10) return false;
        if (hdr[0] != (byte)'I' || hdr[1] != (byte)'D' || hdr[2] != (byte)'3') return false;

        int major = hdr[3];
        int size = SyncSafe(hdr[6], hdr[7], hdr[8], hdr[9]);
        if (size <= 0 || size > 16 * 1024 * 1024) return false;

        var body = new byte[size];
        int read = 0;
        while (read < size)
        {
            int n = fs.Read(body, read, size - read);
            if (n <= 0) break;
            read += n;
        }
        if (read <= 0) return false;

        bool any = false;
        int idLen = major == 2 ? 3 : 4;
        int sizeLen = major == 2 ? 3 : 4;
        int headerLen = major == 2 ? 6 : 10;
        int p = 0;
        while (p + headerLen <= read)
        {
            // A null frame id marks padding.
            if (body[p] == 0) break;
            string id = Encoding.ASCII.GetString(body, p, idLen);
            int frameSize = major == 4
                ? SyncSafe(body[p + 4], body[p + 5], body[p + 6], body[p + 7])
                : major == 2
                    ? (body[p + 3] << 16) | (body[p + 4] << 8) | body[p + 5]
                    : (body[p + 6] << 24) | (body[p + 7] << 16) | (body[p + 8] << 8) | body[p + 9];
            int dataStart = p + headerLen;
            if (frameSize <= 0 || dataStart + frameSize > read) break;

            var field = MapFrame(id);
            if (field is not null)
            {
                var text = DecodeText(body, dataStart, frameSize);
                if (!string.IsNullOrEmpty(text)) { Assign(field.Value, text); any = true; }
            }
            p = dataStart + frameSize;
        }
        return any;
    }

    private static MediaField? MapFrame(string id) => id switch
    {
        "TIT2" or "TT2" => MediaField.Title,
        "TPE1" or "TP1" => MediaField.Artist,
        "TALB" or "TAL" => MediaField.Album,
        "TYER" or "TDRC" or "TYE" => MediaField.Year,
        "TRCK" or "TRK" => MediaField.Track,
        "TCON" or "TCO" => MediaField.Genre,
        "COMM" or "COM" => MediaField.Comment,
        _ => null,
    };

    private void Assign(MediaField f, string value)
    {
        value = value.Trim();
        switch (f)
        {
            case MediaField.Title: Title ??= value; break;
            case MediaField.Artist: Artist ??= value; break;
            case MediaField.Album: Album ??= value; break;
            case MediaField.Year: Year ??= value; break;
            case MediaField.Genre: Genre ??= value; break;
            case MediaField.Track: Track ??= value; break;
            case MediaField.Comment: Comment ??= value; break;
        }
    }

    private static string DecodeText(byte[] b, int start, int len)
    {
        if (len < 1) return string.Empty;
        byte encoding = b[start];
        int textStart = start + 1;
        int textLen = len - 1;
        Encoding enc = encoding switch
        {
            1 => Encoding.Unicode,       // UTF-16 with BOM
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.UTF8,
            _ => Encoding.Latin1,
        };
        var s = enc.GetString(b, textStart, textLen);
        // Strip a leading BOM and trailing nulls.
        return s.Trim('\0', '﻿').Trim();
    }

    private static int SyncSafe(byte a, byte b, byte c, byte d)
        => ((a & 0x7F) << 21) | ((b & 0x7F) << 14) | ((c & 0x7F) << 7) | (d & 0x7F);

    // ── ID3v1 (last 128 bytes of file) ───────────────────────────────────

    private bool ReadId3v1(FileStream fs)
    {
        if (fs.Length < 128) return false;
        fs.Seek(-128, SeekOrigin.End);
        var b = new byte[128];
        if (fs.Read(b, 0, 128) != 128) return false;
        if (b[0] != (byte)'T' || b[1] != (byte)'A' || b[2] != (byte)'G') return false;

        Title ??= Latin(b, 3, 30);
        Artist ??= Latin(b, 33, 30);
        Album ??= Latin(b, 63, 30);
        Year ??= Latin(b, 93, 4);
        Comment ??= Latin(b, 97, 30);
        return true;
    }

    private static string? Latin(byte[] b, int start, int len)
    {
        var s = Encoding.Latin1.GetString(b, start, len).Trim('\0').Trim();
        return s.Length == 0 ? null : s;
    }
}
