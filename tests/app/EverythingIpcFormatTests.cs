using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;
using Item = WhereIsIt.App.Services.EverythingIpcFormat.Item;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Locks the EVERYTHING_IPC_LISTW byte layout that Everything SDK clients
/// (and `es.exe`) expect when they query WhereIsIt over WM_COPYDATA. Any
/// drift in the header order, item shape, or string-offset semantics would
/// silently produce garbled results on the client side.
/// </summary>
public class EverythingIpcFormatTests
{
    [Fact]
    public void BuildList_EmptyRange_EmitsHeaderOnly_NoItemsNoStrings()
    {
        var bytes = EverythingIpcFormat.BuildList(new List<Item>(), start: 0, count: 0, total: 0);
        // 7 DWORDs = 28 bytes, all zero.
        bytes.Length.Should().Be(EverythingIpcFormat.HeaderSize);
        for (int i = 0; i < 28; i++) bytes[i].Should().Be(0);
    }

    [Fact]
    public void BuildList_OneFile_HeaderCountsAndItemAndStringTable()
    {
        var rows = new List<Item> { new("alpha.txt", @"C:\src", IsFolder: false) };
        var bytes = EverythingIpcFormat.BuildList(rows, 0, 1, total: 1);

        // Header
        var br = new BinaryReader(new MemoryStream(bytes));
        br.ReadUInt32().Should().Be(1, "totfolders is rows.Count by convention");
        br.ReadUInt32().Should().Be(0, "totfiles (unused — 0)");
        br.ReadUInt32().Should().Be(1, "totitems");
        br.ReadUInt32().Should().Be(0, "numfolders (0 folders in this window)");
        br.ReadUInt32().Should().Be(1, "numfiles");
        br.ReadUInt32().Should().Be(1, "numitems");
        br.ReadUInt32().Should().Be(0, "offset / start");

        // Item triple
        uint flags = br.ReadUInt32();
        uint nameOff = br.ReadUInt32();
        uint pathOff = br.ReadUInt32();
        flags.Should().Be(0, "file is not a folder");
        nameOff.Should().Be(EverythingIpcFormat.HeaderSize + EverythingIpcFormat.ItemSize);

        // Strings — null-terminated UTF-16.
        var nameBytes = ReadUtf16Z(bytes, (int)nameOff);
        var pathBytes = ReadUtf16Z(bytes, (int)pathOff);
        nameBytes.Should().Be("alpha.txt");
        pathBytes.Should().Be(@"C:\src");
    }

    [Fact]
    public void BuildList_Folder_SetsFolderFlag()
    {
        var rows = new List<Item> { new("logs", @"C:\src", IsFolder: true) };
        var bytes = EverythingIpcFormat.BuildList(rows, 0, 1, total: 1);
        var br = new BinaryReader(new MemoryStream(bytes));
        for (int i = 0; i < 3; i++) br.ReadUInt32();         // skip 3 header DWORDs
        br.ReadUInt32().Should().Be(1, "numfolders");
        br.ReadUInt32().Should().Be(0, "numfiles — 0 files when only folders in window");
        br.ReadUInt32();                                      // numitems
        br.ReadUInt32();                                      // offset
        uint flags = br.ReadUInt32();
        flags.Should().Be(EverythingIpcFormat.EVERYTHING_IPC_FOLDER);
    }

    [Fact]
    public void BuildList_PagedWindow_OffsetAppearsInHeader()
    {
        var rows = new List<Item>
        {
            new("a.txt", @"C:\", false),
            new("b.txt", @"C:\", false),
            new("c.txt", @"C:\", false),
        };
        // Window: skip first 2, take 1.
        var bytes = EverythingIpcFormat.BuildList(rows, start: 2, count: 1, total: 3);
        var br = new BinaryReader(new MemoryStream(bytes));
        for (int i = 0; i < 6; i++) br.ReadUInt32();
        br.ReadUInt32().Should().Be(2, "the requested offset/start must round-trip");

        // The single emitted item must be c.txt, not a.txt.
        for (int i = 0; i < 1; i++) br.ReadUInt32();          // flags
        uint nameOff = br.ReadUInt32();
        ReadUtf16Z(bytes, (int)nameOff).Should().Be("c.txt");
    }

    [Fact]
    public void WriteUtf16Z_TerminatesWithTwoZeroBytes()
    {
        using var ms = new MemoryStream();
        EverythingIpcFormat.WriteUtf16Z(ms, "hi");
        var bytes = ms.ToArray();
        bytes.Length.Should().Be(6, "2 chars × 2 bytes + 2-byte NUL");
        bytes[^2].Should().Be(0);
        bytes[^1].Should().Be(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ReadUtf16Z(byte[] buffer, int offset)
    {
        int end = offset;
        while (end + 1 < buffer.Length && !(buffer[end] == 0 && buffer[end + 1] == 0)) end += 2;
        return Encoding.Unicode.GetString(buffer, offset, end - offset);
    }
}
