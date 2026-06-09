using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WhereIsIt.App.Services;

/// <summary>
/// Builds the <c>EVERYTHING_IPC_LISTW</c> byte payload that
/// <see cref="EverythingIpcServer"/> ships back over <c>WM_COPYDATA</c> to
/// Everything SDK clients. The encoder is pure managed code (no Win32) so
/// xUnit can verify byte-for-byte parity with the documented SDK shape
/// without spinning up a message-only window.
///
/// Wire format (Everything_IPC.h, EVERYTHING_IPC_LISTW):
/// <code>
///   DWORD totfolders;
///   DWORD totfiles;          // 0 — we track folder count only
///   DWORD totitems;
///   DWORD numfolders;
///   DWORD numfiles;
///   DWORD numitems;
///   DWORD offset;
///   ITEM items[numitems];    // { DWORD flags; DWORD nameOff; DWORD pathOff; }
///   WCHAR strings[...];      // null-terminated; offsets are bytes from start
/// </code>
/// </summary>
public static class EverythingIpcFormat
{
    public const uint EVERYTHING_IPC_FOLDER = 0x00000001;
    public const int  HeaderSize = 7 * 4;
    public const int  ItemSize   = 3 * 4;

    public readonly record struct Item(string Name, string Parent, bool IsFolder);

    /// <summary>Serialise a windowed range of rows into the IPC payload.</summary>
    public static byte[] BuildList(IReadOnlyList<Item> rows, int start, int count, int total)
    {
        int stringsStart = HeaderSize + count * ItemSize;

        using var strings = new MemoryStream();
        var records = new (uint Flags, uint NameOff, uint PathOff)[count];
        int folders = 0, files = 0;
        for (int i = 0; i < count; i++)
        {
            var row = rows[start + i];
            if (row.IsFolder) folders++; else files++;
            uint nameOff = (uint)(stringsStart + strings.Length);
            WriteUtf16Z(strings, row.Name);
            uint pathOff = (uint)(stringsStart + strings.Length);
            WriteUtf16Z(strings, row.Parent);
            records[i] = (row.IsFolder ? EVERYTHING_IPC_FOLDER : 0u, nameOff, pathOff);
        }

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((uint)rows.Count);  // totfolders — best-effort (real folder/file split not stored globally)
        bw.Write((uint)0);            // totfiles
        bw.Write((uint)total);        // totitems
        bw.Write((uint)folders);
        bw.Write((uint)files);
        bw.Write((uint)count);
        bw.Write((uint)start);        // offset
        foreach (var r in records) { bw.Write(r.Flags); bw.Write(r.NameOff); bw.Write(r.PathOff); }
        bw.Write(strings.ToArray());
        bw.Flush();
        return ms.ToArray();
    }

    public static void WriteUtf16Z(Stream s, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        s.Write(bytes, 0, bytes.Length);
        s.WriteByte(0); s.WriteByte(0);  // WCHAR null terminator
    }
}
