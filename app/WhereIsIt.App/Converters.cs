using Microsoft.UI.Xaml;

namespace WhereIsIt.App;

public static class Converters
{
    public static Visibility WhenTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility WhenFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility WhenNonEmpty(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
}
