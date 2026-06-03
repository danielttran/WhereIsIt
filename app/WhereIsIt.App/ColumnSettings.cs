using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace WhereIsIt.App;

/// <summary>
/// Live, observable view settings — column visibility plus thumbnail size.
/// XAML binds to <c>app:ColumnSettings.Current.Xxx</c> with Mode=OneWay so
/// toggling a checkmark in the View menu reflows the result grid immediately
/// (no restart). A singleton holds the state; AppBootstrap seeds it from
/// AppSettings on launch and the View menu mutates it directly.
/// </summary>
public partial class ColumnSettings : ObservableObject
{
    public static ColumnSettings Current { get; } = new();

    // ── User-visible knobs (set by the View menu) ────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatedColumnWidth), nameof(CreatedColumnVisibility))]
    private bool showCreatedColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessedColumnWidth), nameof(AccessedColumnVisibility))]
    private bool showAccessedColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunCountColumnWidth), nameof(RunCountColumnVisibility))]
    private bool showRunCountColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPaneWidth), nameof(PreviewPaneVisibility))]
    private bool showPreviewPane;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DimensionsColumnWidth), nameof(DimensionsColumnVisibility))]
    private bool showDimensionsColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistColumnWidth), nameof(ArtistColumnVisibility))]
    private bool showArtistColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumColumnWidth), nameof(AlbumColumnVisibility))]
    private bool showAlbumColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthorColumnWidth), nameof(AuthorColumnVisibility))]
    private bool showAuthorColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbnailColumnWidth), nameof(ThumbnailColumnVisibility), nameof(ThumbnailRenderSize),
        nameof(IsThumbOff), nameof(IsThumbSmall), nameof(IsThumbMedium), nameof(IsThumbLarge), nameof(IsThumbXL))]
    private int thumbnailSizePx;

    // Radio-button-style flags bound by each Thumbnails menu item's IsChecked.
    public bool IsThumbOff    => ThumbnailSizePx == 0;
    public bool IsThumbSmall  => ThumbnailSizePx == 32;
    public bool IsThumbMedium => ThumbnailSizePx == 64;
    public bool IsThumbLarge  => ThumbnailSizePx == 128;
    public bool IsThumbXL     => ThumbnailSizePx == 256;

    // ── XAML-bound derived properties ────────────────────────────────────

    public GridLength CreatedColumnWidth  => ShowCreatedColumn  ? new GridLength(140) : new GridLength(0);
    public GridLength AccessedColumnWidth => ShowAccessedColumn ? new GridLength(140) : new GridLength(0);
    public GridLength RunCountColumnWidth => ShowRunCountColumn ? new GridLength(80)  : new GridLength(0);
    public GridLength ThumbnailColumnWidth => ThumbnailSizePx > 0 ? new GridLength(ThumbnailSizePx + 16) : new GridLength(0);

    public Visibility CreatedColumnVisibility  => ShowCreatedColumn  ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AccessedColumnVisibility => ShowAccessedColumn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunCountColumnVisibility => ShowRunCountColumn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbnailColumnVisibility => ThumbnailSizePx > 0 ? Visibility.Visible : Visibility.Collapsed;

    public double ThumbnailRenderSize => ThumbnailSizePx > 0 ? ThumbnailSizePx : 0d;

    public GridLength PreviewPaneWidth => ShowPreviewPane ? new GridLength(320) : new GridLength(0);
    public Visibility PreviewPaneVisibility => ShowPreviewPane ? Visibility.Visible : Visibility.Collapsed;

    public GridLength DimensionsColumnWidth => ShowDimensionsColumn ? new GridLength(110) : new GridLength(0);
    public GridLength ArtistColumnWidth     => ShowArtistColumn     ? new GridLength(160) : new GridLength(0);
    public GridLength AlbumColumnWidth      => ShowAlbumColumn      ? new GridLength(160) : new GridLength(0);
    public GridLength AuthorColumnWidth     => ShowAuthorColumn     ? new GridLength(160) : new GridLength(0);

    public Visibility DimensionsColumnVisibility => ShowDimensionsColumn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ArtistColumnVisibility     => ShowArtistColumn     ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AlbumColumnVisibility      => ShowAlbumColumn      ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AuthorColumnVisibility     => ShowAuthorColumn     ? Visibility.Visible : Visibility.Collapsed;
}
