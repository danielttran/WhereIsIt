using Microsoft.UI.Xaml;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

/// <summary>
/// WinUI projection of the column-visibility booleans on
/// <see cref="ResultRowViewModel"/>. Lives in the App project because Core
/// can't reference WinUI types. XAML binds <c>ColumnDefinition.Width</c> and
/// header <c>Visibility</c> to these via <c>x:Bind ... Mode=OneTime</c>, so
/// the toggle takes effect on next app launch.
/// </summary>
public static class ColumnSettings
{
    public static GridLength CreatedColumnWidth
        => ResultRowViewModel.ShowCreatedColumn  ? new GridLength(140) : new GridLength(0);

    public static GridLength AccessedColumnWidth
        => ResultRowViewModel.ShowAccessedColumn ? new GridLength(140) : new GridLength(0);

    public static Visibility CreatedColumnVisibility
        => ResultRowViewModel.ShowCreatedColumn  ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility AccessedColumnVisibility
        => ResultRowViewModel.ShowAccessedColumn ? Visibility.Visible : Visibility.Collapsed;

    public static GridLength RunCountColumnWidth
        => ResultRowViewModel.ShowRunCountColumn ? new GridLength(80) : new GridLength(0);

    public static Visibility RunCountColumnVisibility
        => ResultRowViewModel.ShowRunCountColumn ? Visibility.Visible : Visibility.Collapsed;
}
