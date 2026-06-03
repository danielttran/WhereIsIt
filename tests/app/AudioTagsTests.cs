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
