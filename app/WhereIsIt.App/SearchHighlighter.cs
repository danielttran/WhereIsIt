using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace WhereIsIt.App;

/// <summary>
/// Attached behaviour that marks up matched query terms inside a
/// <see cref="TextBlock"/> using WinUI's <see cref="TextHighlighter"/> ranges.
///
/// The visible text is funnelled through <see cref="TextProperty"/> instead of
/// the TextBlock's own <c>Text</c> binding so that highlights are recomputed
/// both when the text changes (a recycled list container binds a new row) and
/// when the active terms change (a new search). Terms arrive newline-separated
/// in <see cref="TermsProperty"/>; matching is case-insensitive and overlapping
/// spans are merged by advancing past each hit.
/// </summary>
public static class SearchHighlighter
{
    // Translucent amber so the underlying themed text stays legible in both
    // light and dark modes (a TextHighlighter paints behind the glyphs).
    private static readonly Brush HighlightBrush =
        new SolidColorBrush(ColorHelper.FromArgb(0x80, 0xFF, 0xD5, 0x4F));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(SearchHighlighter),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TermsProperty =
        DependencyProperty.RegisterAttached(
            "Terms", typeof(string), typeof(SearchHighlighter),
            new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);

    public static string? GetTerms(DependencyObject o) => (string?)o.GetValue(TermsProperty);
    public static void SetTerms(DependencyObject o, string? v) => o.SetValue(TermsProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var text = GetText(tb) ?? string.Empty;
        // Keep the visible text in sync — this attached property owns it.
        if (tb.Text != text) tb.Text = text;
        Apply(tb, text, GetTerms(tb));
    }

    private static void Apply(TextBlock tb, string text, string? termsRaw)
    {
        tb.TextHighlighters.Clear();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(termsRaw)) return;

        var terms = termsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var highlighter = new TextHighlighter { Background = HighlightBrush };

        foreach (var term in terms)
        {
            if (term.Length == 0) continue;
            int idx = 0;
            while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                highlighter.Ranges.Add(new TextRange { StartIndex = idx, Length = term.Length });
                idx += term.Length;
            }
        }

        if (highlighter.Ranges.Count > 0) tb.TextHighlighters.Add(highlighter);
    }
}
