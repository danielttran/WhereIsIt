using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WhereIsIt.App.Services;

namespace WhereIsIt.App;

/// <summary>
/// Fetches Windows-shell thumbnails for result rows. Backed by a generic
/// <see cref="ThumbnailCache{T}"/> from Core so the cache logic stays unit-
/// tested without WinUI references.
///
/// Hot-path contract: every fetch carries a CancellationToken keyed off the
/// row's container lifetime. When a ListView container recycles, the caller
/// cancels — so a fast scroll never piles up work, and a returning row can
/// re-request at the new size without leaking the old one.
/// </summary>
public sealed class ThumbnailService
{
    private readonly ThumbnailCache<ImageSource> cache = new(capacity: 256);

    public ThumbnailSize CurrentSize { get; set; }

    public bool TryGetCached(string path, ThumbnailSize size, out ImageSource? bitmap)
        => cache.TryGet(path, size, out bitmap);

    public async Task<ImageSource?> GetAsync(string path, ThumbnailSize size, CancellationToken ct)
    {
        if (size == ThumbnailSize.Off || string.IsNullOrEmpty(path))
            return null;
        if (cache.TryGet(path, size, out var cached))
            return cached;
        try
        {
            ct.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            using var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, (uint)(int)size, ThumbnailOptions.UseCurrentScale).AsTask(ct);
            if (thumb is null || thumb.Size == 0) return null;
            ct.ThrowIfCancellationRequested();

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumb).AsTask(ct);
            cache.Put(path, size, bitmap);
            return bitmap;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }   // unreachable paths, AV files, etc.
    }

    public void Clear() => cache.Clear();
}
