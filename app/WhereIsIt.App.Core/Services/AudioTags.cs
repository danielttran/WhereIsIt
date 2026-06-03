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
            bool any = tags.ReadId3v2(fs);
            if (!tags.Complete) any |= tags.ReadId3v1(fs);
            return any;
        }
        catch { return false; }
    }

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
