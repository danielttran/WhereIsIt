using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WhereIsIt.App;

public static class Converters
{
    public static Visibility WhenTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility WhenFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility WhenNonEmpty(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Adapts the loosely-typed <c>object?</c> ThumbnailSource on
    /// ResultRowViewModel (Core can't reference WinUI) to <c>ImageSource?</c>
    /// for <c>Image.Source</c>. Null and non-ImageSource values pass through
    /// as null so the Image element stays empty.
    /// </summary>
    public static ImageSource? AsImageSource(object? value) => value as ImageSource;
}
