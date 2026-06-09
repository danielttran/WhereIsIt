using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Deterministic-random file tree for end-to-end feature tests. Single
/// PRNG seed reproduces the same names/sizes/dates, so every test class
/// can pull the same set of probe files without flake. Disposing the
/// fixture removes the tree.
///
/// The tree is broad on purpose: each feature in the parser/decorator
/// stack has at least one file that exercises it (varied extensions, name
/// patterns, sizes, mtimes, attributes, nesting depth, duplicates).
/// </summary>
public sealed class RandomFileFixture : IDisposable
{
    public DirectoryInfo Root { get; }
    public IReadOnlyList<FileInfo> Files => createdFiles;
    private readonly List<FileInfo> createdFiles = new();
    private readonly Random rng;

    // xUnit's class-fixture activator dislikes optional ctor args — keep
    // this single signature parameterless.
    public RandomFileFixture()
    {
        rng = new Random(4242);
        var basePath = Path.Combine(Path.GetTempPath(), $"wii-rand-{Guid.NewGuid():N}");
        if (Directory.Exists(basePath)) Directory.Delete(basePath, recursive: true);
        Root = Directory.CreateDirectory(basePath);
        Seed();
    }

    private void Seed()
    {
        // ── 1. Per-extension sample (covers ext:, *.<ext>, type-macro filters)
        var exts = new[] { "md", "txt", "log", "cs", "py", "js", "json",
                           "mp3", "mp4", "jpg", "png", "zip", "pdf", "doc" };
        foreach (var ext in exts)
        {
            var name = $"sample_{ext}_a.{ext}";
            WriteRandomBytes(Path.Combine(Root.FullName, name), 256 + rng.Next(2048));
        }

        // ── 2. Size buckets (size: filter)
        WriteRandomBytes(Path.Combine(Root.FullName, "tiny.bin"),  100);          // <1KB
        WriteRandomBytes(Path.Combine(Root.FullName, "small.bin"), 50 * 1024);    // ~50KB
        WriteRandomBytes(Path.Combine(Root.FullName, "medium.bin"), 2 * 1024 * 1024); // ~2MB

        // ── 3. Date buckets (dm:/dc:/da:)
        var oldFile = Path.Combine(Root.FullName, "old.txt");
        WriteRandomBytes(oldFile, 64);
        File.SetLastWriteTime(oldFile, DateTime.Now.AddYears(-5));
        File.SetCreationTime(oldFile, DateTime.Now.AddYears(-5));
        var freshFile = Path.Combine(Root.FullName, "fresh.txt");
        WriteRandomBytes(freshFile, 64);

        // ── 4. Attributes: hidden + read-only (attrib:)
        var hidden = Path.Combine(Root.FullName, "secret.cfg");
        WriteRandomBytes(hidden, 32);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var ro = Path.Combine(Root.FullName, "frozen.txt");
        WriteRandomBytes(ro, 32);
        File.SetAttributes(ro, File.GetAttributes(ro) | FileAttributes.ReadOnly);

        // ── 5. Nested folders + depth (depth:, child:, parent:)
        for (int d = 0; d < 4; d++)
        {
            var dir = Root.FullName;
            for (int k = 0; k <= d; k++) dir = Path.Combine(dir, "lvl" + k);
            Directory.CreateDirectory(dir);
            WriteRandomBytes(Path.Combine(dir, $"deep_{d}.note"), 96);
        }

        // ── 6. Name-prefix / suffix (startwith:/endwith:/wfn:)
        WriteRandomBytes(Path.Combine(Root.FullName, "PREFIX_report.log"), 200);
        WriteRandomBytes(Path.Combine(Root.FullName, "scoreboard_SUFFIX.dat"), 200);
        WriteRandomBytes(Path.Combine(Root.FullName, "alpha bravo charlie.txt"), 200);

        // ── 7. Duplicates (dupe:, sizedupe:, namepartdupe:)
        for (int i = 0; i < 3; i++)
            WriteRandomBytes(Path.Combine(Root.FullName, $"dup_{i}.bin"), 1024);
        Directory.CreateDirectory(Path.Combine(Root.FullName, "alt"));
        File.Copy(Path.Combine(Root.FullName, "dup_0.bin"),
                  Path.Combine(Root.FullName, "alt", "dup_0.bin"), overwrite: true);

        // ── 8. Empty files + empty folder (empty:)
        File.WriteAllBytes(Path.Combine(Root.FullName, "blank.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(Root.FullName, "EmptyDir"));

        // ── 9. Unicode + spaces (tokenizer robustness)
        WriteRandomBytes(Path.Combine(Root.FullName, "тест-юникод.txt"), 48);

        // ── 10. Snapshot every file that exists right now.
        foreach (var f in Root.EnumerateFiles("*", SearchOption.AllDirectories))
            createdFiles.Add(f);
    }

    private void WriteRandomBytes(string path, int length)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var buf = new byte[length];
        rng.NextBytes(buf);
        File.WriteAllBytes(path, buf);
    }

    public void Dispose()
    {
        try
        {
            // Strip read-only bit / hidden so Delete actually proceeds.
            foreach (var f in Root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f.FullName, FileAttributes.Normal); } catch { }
            }
            Root.Delete(recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }
}
