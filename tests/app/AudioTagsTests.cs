using System.IO;
using System.Text;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class AudioTagsTests
{
    // Builds a 128-byte ID3v1 trailer.
    private static byte[] Id3v1(string title, string artist, string album, string year)
    {
        var b = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(b, 0);
        Put(b, 3, title, 30);
        Put(b, 33, artist, 30);
        Put(b, 63, album, 30);
        Put(b, 93, year, 4);
        return b;
    }

    private static void Put(byte[] b, int offset, string s, int max)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i < bytes.Length && i < max; i++) b[offset + i] = bytes[i];
    }

    private static string WriteTemp(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), "whereisit-id3-" + System.Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void TryRead_Id3v1_ReadsFields()
    {
        var path = WriteTemp(Id3v1("Money", "Pink Floyd", "Dark Side", "1973"));
        try
        {
            AudioTags.TryRead(path, out var tags).Should().BeTrue();
            tags.Title.Should().Be("Money");
            tags.Artist.Should().Be("Pink Floyd");
            tags.Album.Should().Be("Dark Side");
            tags.Year.Should().Be("1973");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Match_SubstringCaseInsensitive()
    {
        var path = WriteTemp(Id3v1("Money", "Pink Floyd", "Dark Side", "1973"));
        try
        {
            AudioTags.Match([new MediaFilter(MediaField.Artist, "floyd")], path, false, true)
                .Should().BeTrue();
            AudioTags.Match([new MediaFilter(MediaField.Album, "dark")], path, false, true)
                .Should().BeTrue();
            AudioTags.Match([new MediaFilter(MediaField.Artist, "zeppelin")], path, false, true)
                .Should().BeFalse();
        }
        finally { File.Delete(path); }
    }

    private static byte[] Flac(params string[] comments)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("fLaC"));
        using var body = new MemoryStream();
        WriteLe32(body, 0);                // vendor length
        WriteLe32(body, comments.Length);  // comment count
        foreach (var c in comments)
        {
            var cb = Encoding.UTF8.GetBytes(c);
            WriteLe32(body, cb.Length);
            body.Write(cb);
        }
        var arr = body.ToArray();
        ms.WriteByte(0x84);                          // last block + type 4 (VORBIS_COMMENT)
        ms.WriteByte((byte)((arr.Length >> 16) & 0xFF));
        ms.WriteByte((byte)((arr.Length >> 8) & 0xFF));
        ms.WriteByte((byte)(arr.Length & 0xFF));
        ms.Write(arr);
        return ms.ToArray();
    }

    private static void WriteLe32(Stream s, int v)
    {
        s.WriteByte((byte)(v & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 24) & 0xFF));
    }

    private static byte[] VorbisComments(params string[] comments)
    {
        using var ms = new MemoryStream();
        WriteLe32(ms, 0);                 // vendor length
        WriteLe32(ms, comments.Length);   // comment count
        foreach (var c in comments)
        {
            var cb = Encoding.UTF8.GetBytes(c);
            WriteLe32(ms, cb.Length);
            ms.Write(cb);
        }
        return ms.ToArray();
    }

    [Fact]
    public void TryRead_Ogg_ReadsVorbisComments()
    {
        // "OggS" magic, filler, then the 0x03"vorbis" comment-header packet.
        var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("OggS"));
        file.Write(new byte[20]);
        file.WriteByte(0x03);
        file.Write(Encoding.ASCII.GetBytes("vorbis"));
        file.Write(VorbisComments("ARTIST=Beck", "TITLE=Loser"));
        var path = WriteTemp(file.ToArray());
        try
        {
            AudioTags.TryRead(path, out var tags).Should().BeTrue();
            tags.Artist.Should().Be("Beck");
            tags.Title.Should().Be("Loser");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryRead_Flac_ReadsVorbisComments()
    {
        var path = WriteTemp(Flac("ARTIST=Daft Punk", "ALBUM=Discovery", "TITLE=One More Time"));
        try
        {
            AudioTags.TryRead(path, out var tags).Should().BeTrue();
            tags.Artist.Should().Be("Daft Punk");
            tags.Album.Should().Be("Discovery");
            tags.Title.Should().Be("One More Time");
        }
        finally { File.Delete(path); }
    }

    // ── M4A / MP4 atom builders ──────────────────────────────────────────

    private static byte[] Atom(string type, byte[] content) => AtomRaw(Encoding.ASCII.GetBytes(type), content);

    private static byte[] AtomRaw(byte[] type4, byte[] content)
    {
        int size = 8 + content.Length;
        var b = new byte[size];
        b[0] = (byte)(size >> 24); b[1] = (byte)(size >> 16); b[2] = (byte)(size >> 8); b[3] = (byte)size;
        type4.CopyTo(b, 4);
        content.CopyTo(b, 8);
        return b;
    }

    private static byte[] DataAtom(string value)
    {
        var v = Encoding.UTF8.GetBytes(value);
        var content = new byte[8 + v.Length];
        content[3] = 1; // well-known type = UTF-8 text
        v.CopyTo(content, 8);
        return Atom("data", content);
    }

    private static byte[] Cat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    [Fact]
    public void TryRead_M4a_ReadsItunesAtoms()
    {
        var art = AtomRaw([0xA9, (byte)'A', (byte)'R', (byte)'T'], DataAtom("Radiohead"));
        var alb = AtomRaw([0xA9, (byte)'a', (byte)'l', (byte)'b'], DataAtom("OK Computer"));
        var ilst = Atom("ilst", Cat(art, alb));
        var meta = Atom("meta", Cat(new byte[4], ilst)); // 4-byte version/flags prefix
        var udta = Atom("udta", meta);
        var moov = Atom("moov", udta);
        var ftyp = Atom("ftyp", new byte[8]);
        var path = WriteTemp(Cat(ftyp, moov));
        try
        {
            AudioTags.TryRead(path, out var tags).Should().BeTrue();
            tags.Artist.Should().Be("Radiohead");
            tags.Album.Should().Be("OK Computer");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Match_NonAudioFile_ReturnsFalse()
    {
        var path = WriteTemp(Encoding.ASCII.GetBytes("just some text, definitely not an mp3 tag"));
        try
        {
            AudioTags.Match([new MediaFilter(MediaField.Artist, "x")], path, false, true)
                .Should().BeFalse();
        }
        finally { File.Delete(path); }
    }
}
