using System;
using System.Collections.Generic;

namespace WhereIsIt.App.Services;

/// <summary>
/// File-system thumbnails: which sizes the UI offers, plus a small LRU cache
/// so revisiting a row (after a scroll-back) shows its bitmap instantly
/// without re-hitting the Windows shell thumbnail provider.
///
/// Pure C# — no WinUI types — so this is unit-testable. The actual bitmap
/// type is generic so the App project can substitute its WinUI BitmapImage.
/// </summary>
public enum ThumbnailSize
{
    Off       = 0,
    Small     = 32,
    Medium    = 64,
    Large     = 128,
    ExtraLarge = 256,
}

public sealed class ThumbnailCache<TBitmap> where TBitmap : class
{
    private readonly int capacity;
    private readonly Dictionary<Key, LinkedListNode<Entry>> map;
    private readonly LinkedList<Entry> lru = new();
    private readonly object gate = new();

    // Capped at 256 because an ExtraLarge entry holds a 256×256 BitmapImage
    // ≈ 256 KB of decoded pixel data — at the old default the worst-case
    // resident memory crossed 250 MB after enough scrolling. 256 entries
    // keeps the worst case under ~70 MB and remains plenty for the visible
    // viewport plus a generous over-fetch margin.
    public ThumbnailCache(int capacity = 256)
    {
        this.capacity = capacity < 1 ? 1 : capacity;
        map = new Dictionary<Key, LinkedListNode<Entry>>(this.capacity);
    }

    public int Count
    {
        get { lock (gate) return map.Count; }
    }

    public bool TryGet(string path, ThumbnailSize size, out TBitmap? bitmap)
    {
        var key = new Key(path, size);
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                lru.Remove(node);
                lru.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }
        bitmap = null;
        return false;
    }

    public void Put(string path, ThumbnailSize size, TBitmap bitmap)
    {
        if (string.IsNullOrEmpty(path) || bitmap is null) return;
        var key = new Key(path, size);
        lock (gate)
        {
            if (map.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, bitmap);
                lru.Remove(existing);
                lru.AddFirst(existing);
                return;
            }
            var node = new LinkedListNode<Entry>(new Entry(key, bitmap));
            lru.AddFirst(node);
            map[key] = node;
            while (map.Count > capacity && lru.Last is { } tail)
            {
                map.Remove(tail.Value.Key);
                lru.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            map.Clear();
            lru.Clear();
        }
    }

    private readonly record struct Key(string Path, ThumbnailSize Size);
    private record struct Entry(Key Key, TBitmap Bitmap);
}
