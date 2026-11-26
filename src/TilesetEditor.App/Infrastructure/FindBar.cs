using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TilesetEditor.App.Infrastructure;

/// <summary>
/// Ctrl+F in every multi-line text box of the app (scripts, code, build output, previews): a small bar at the top right
/// of the box – search text, ↑/↓ (Enter / Shift+Enter, F3 / Shift+F3), match case, "n of m" – and, where the text can be
/// edited, Replace / Replace all (Ctrl+H opens it with the replace field). Esc closes it and puts the cursor at the match.
/// </summary>
public static class FindBar
{
    private static Popup? _popup;
    private static TextBox? _target;
    private static readonly TextBox Query = new() { Width = 180, VerticalContentAlignment = VerticalAlignment.Center };
    private static readonly TextBox Replacement = new() { Width = 180, VerticalContentAlignment = VerticalAlignment.Center };
    private static readonly CheckBox MatchCase = new() { Content = "Aa", ToolTip = "Match case", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private static readonly TextBlock Count = new() { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private static readonly StackPanel ReplaceRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>Hooks Ctrl+F / Ctrl+H / F3 into every TextBox of the app (once, at start).</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKey));
        Query.TextChanged += (_, _) => { if (_target != null) Find(true, fromSelectionStart: true); };
        Query.PreviewKeyDown += QueryKeys;
        Replacement.PreviewKeyDown += QueryKeys;
        MatchCase.Checked += (_, _) => Find(true, fromSelectionStart: true);
        MatchCase.Unchecked += (_, _) => Find(true, fromSelectionStart: true);
    }

    private static bool IsSearchable(TextBox box) => box != Query && box != Replacement && (box.AcceptsReturn || box.LineCount > 1 || box.Text.Contains('\n'));

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || !IsSearchable(box)) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.F) { Open(box, replace: false); e.Handled = true; }
        else if (ctrl && e.Key == Key.H && !box.IsReadOnly) { Open(box, replace: true); e.Handled = true; }
        else if (e.Key == Key.F3 && Query.Text.Length > 0) { _target = box; Find(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
        else if (e.Key == Key.Escape && _popup?.IsOpen == true && _target == box) { Close(); e.Handled = true; }
    }

    private static void QueryKeys(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Enter when sender == Replacement && !shift: ReplaceOne(); e.Handled = true; break;
            case Key.Enter: Find(!shift); e.Handled = true; break;
            case Key.F3: Find(!shift); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    /// <summary>
    /// Opens the bar on <paramref name="box"/> with <paramref name="query"/> and selects its first match at or after
    /// <paramref name="from"/> (e.g. a text id in a script file); every match is highlighted.
    /// </summary>
    public static void ShowMatch(TextBox box, string query, int from = 0)
    {
        _popup ??= Build();
        Query.Text = query;
        Open(box, replace: false, keepQuery: true);
        int at = box.Text.IndexOf(query, Math.Clamp(from, 0, box.Text.Length), StringComparison.Ordinal);
        if (at < 0) at = box.Text.IndexOf(query, StringComparison.Ordinal);
        if (at >= 0) box.Select(at, query.Length);
        UpdateCount();
        Highlight();
        // Scroll once the box has laid out its (possibly new) text.
        if (at >= 0)
            box.Dispatcher.BeginInvoke(() =>
            {
                try { box.ScrollToLine(box.GetLineIndexFromCharacterIndex(Math.Min(at, box.Text.Length))); }
                catch (ArgumentOutOfRangeException) { }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static void Open(TextBox box, bool replace, bool keepQuery = false)
    {
        if (_target != null && _target != box) Unhighlight();
        _target = box;
        box.IsInactiveSelectionHighlightEnabled = true;   // the match stays visible while typing in the bar
        _popup ??= Build();
        ReplaceRow.Visibility = replace || !box.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        ReplaceRow.IsEnabled = !box.IsReadOnly;
        if (!keepQuery && box.SelectionLength is > 0 and < 200 && !box.SelectedText.Contains('\n')) Query.Text = box.SelectedText;
        _popup.PlacementTarget = box;
        _popup.HorizontalOffset = Math.Max(0, box.ActualWidth - 470);
        _popup.IsOpen = true;
        var focus = replace ? Replacement : Query;
        focus.Dispatcher.BeginInvoke(() => { focus.Focus(); focus.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        UpdateCount();
        Highlight();
    }

    private static Popup Build()
    {
        var close = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Close (Esc)" };
        close.Click += (_, _) => Close();
        var up = new Button { Content = "↑", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Previous (Shift+Enter, Shift+F3)" };
        up.Click += (_, _) => Find(false);
        var down = new Button { Content = "↓", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 0, 0), ToolTip = "Next (Enter, F3)" };
        down.Click += (_, _) => Find(true);
        var one = new Button { Content = "Replace", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Replace this match and go to the next (Enter in the field)" };
        one.Click += (_, _) => ReplaceOne();
        var all = new Button { Content = "All", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 0, 0), ToolTip = "Replace every match" };
        all.Click += (_, _) => ReplaceAll();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "🔍 ", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Query);
        row.Children.Add(MatchCase);
        row.Children.Add(up);
        row.Children.Add(down);
        row.Children.Add(Count);
        row.Children.Add(close);
        ReplaceRow.Children.Add(new TextBlock { Text = "↪ ", VerticalAlignment = VerticalAlignment.Center });
        ReplaceRow.Children.Add(Replacement);
        ReplaceRow.Children.Add(one);
        ReplaceRow.Children.Add(all);
        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(row);
        panel.Children.Add(ReplaceRow);
        var border = new Border
        {
            Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Background = (Brush?)Application.Current.TryFindResource("SurfaceBrush") ?? Brushes.White,
            BorderBrush = (Brush?)Application.Current.TryFindResource("BorderBrush") ?? Brushes.Gray,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return new Popup { Child = border, Placement = PlacementMode.Relative, VerticalOffset = 4, StaysOpen = true, AllowsTransparency = true };
    }

    private static void Close()
    {
        if (_popup != null) _popup.IsOpen = false;
        Unhighlight();
        if (_target is { } t) { t.Focus(); t.IsInactiveSelectionHighlightEnabled = false; }
    }

    private static StringComparison Comparison => MatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Selects the next (or previous) match, wrapping around the end.</summary>
    private static void Find(bool forward, bool fromSelectionStart = false)
    {
        var box = _target;
        var q = Query.Text;
        if (box == null) return;
        if (q.Length == 0) { UpdateCount(); return; }
        var text = box.Text;
        int at;
        if (forward)
        {
            int start = fromSelectionStart ? box.SelectionStart : box.SelectionStart + Math.Max(1, box.SelectionLength);
            at = start <= text.Length ? text.IndexOf(q, Math.Min(start, text.Length), Comparison) : -1;
            if (at < 0) at = text.IndexOf(q, Comparison);
        }
        else
        {
            int start = box.SelectionStart - 1;
            at = start >= 0 ? text.LastIndexOf(q, start, Comparison) : -1;
            if (at < 0) at = text.LastIndexOf(q, Comparison);
        }
        if (at >= 0)
        {
            box.Select(at, q.Length);
            try { box.ScrollToLine(box.GetLineIndexFromCharacterIndex(at)); }
            catch (ArgumentOutOfRangeException) { }
        }
        UpdateCount();
    }

    private static void UpdateCount()
    {
        var box = _target;
        var q = Query.Text;
        if (box == null || q.Length == 0) { Count.Text = ""; return; }
        int total = 0, current = 0;
        for (int i = box.Text.IndexOf(q, Comparison); i >= 0; i = box.Text.IndexOf(q, i + q.Length, Comparison))
        {
            total++;
            if (i == box.SelectionStart && box.SelectionLength == q.Length) current = total;
        }
        Count.Text = total == 0 ? "no match" : current > 0 ? $"{current} of {total}" : $"{total} found";
        Highlight();
        Count.Foreground = total == 0 ? Brushes.IndianRed : (Brush?)Application.Current.TryFindResource("InkBrush") ?? Brushes.Black;
    }

    private static void ReplaceOne()
    {
        var box = _target;
        if (box == null || box.IsReadOnly || Query.Text.Length == 0) return;
        if (box.SelectionLength == Query.Text.Length && string.Equals(box.SelectedText, Query.Text, Comparison))
        {
            int at = box.SelectionStart;
            box.SelectedText = Replacement.Text;   // one undo step in the box, raises TextChanged like typing
            box.Select(at + Replacement.Text.Length, 0);
        }
        Find(true);
    }

    private static void ReplaceAll()
    {
        var box = _target;
        if (box == null || box.IsReadOnly || Query.Text.Length == 0) return;
        var text = box.Text;
        int n = 0;
        var sb = new System.Text.StringBuilder();
        int last = 0;
        for (int i = text.IndexOf(Query.Text, Comparison); i >= 0; i = text.IndexOf(Query.Text, i + Query.Text.Length, Comparison))
        {
            sb.Append(text, last, i - last).Append(Replacement.Text);
            last = i + Query.Text.Length;
            n++;
        }
        if (n == 0) { UpdateCount(); return; }
        sb.Append(text, last, text.Length - last);
        int caret = box.CaretIndex;
        box.SelectAll();
        box.SelectedText = sb.ToString();       // one undo step
        box.Select(Math.Min(caret, box.Text.Length), 0);
        Count.Text = $"{n} replaced";
    }

    // ------------------------------------------------------------------ highlighting every match

    private static MatchAdorner? _adorner;

    private static void Highlight()
    {
        var box = _target;
        if (box == null || _popup?.IsOpen != true) return;
        if (_adorner == null || _adorner.AdornedElement != box)
        {
            Unhighlight();
            if (AdornerLayer.GetAdornerLayer(box) is not { } layer) return;
            _adorner = new MatchAdorner(box);
            layer.Add(_adorner);
        }
        _adorner.Query = Query.Text;
        _adorner.Comparison = Comparison;
        _adorner.InvalidateVisual();
    }

    private static void Unhighlight()
    {
        if (_adorner == null) return;
        AdornerLayer.GetAdornerLayer(_adorner.AdornedElement)?.Remove(_adorner);
        _adorner.Detach();
        _adorner = null;
    }

    /// <summary>Yellow boxes over every visible match, orange over the selected one; follows scrolling and typing.</summary>
    private sealed class MatchAdorner : Adorner
    {
        private readonly TextBox _box;
        private static readonly Brush All = new SolidColorBrush(Color.FromArgb(110, 255, 220, 40));
        private static readonly Brush Current = new SolidColorBrush(Color.FromArgb(150, 255, 140, 0));
        public string Query { get; set; } = "";
        public StringComparison Comparison { get; set; } = StringComparison.OrdinalIgnoreCase;

        public MatchAdorner(TextBox box) : base(box)
        {
            _box = box;
            IsHitTestVisible = false;
            _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Redraw));
            _box.TextChanged += Redraw;
            _box.SelectionChanged += Redraw;
            _box.SizeChanged += Redraw;
        }

        private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

        public void Detach()
        {
            _box.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Redraw));
            _box.TextChanged -= Redraw;
            _box.SelectionChanged -= Redraw;
            _box.SizeChanged -= Redraw;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Query.Length == 0 || !_box.IsVisible || _box.LineCount <= 0) return;
            try { Draw(dc); }
            catch (ArgumentOutOfRangeException)
            {
                // The box was in the middle of a new layout (e.g. right after its text was set): draw again once it is done.
                if (!_retry)
                {
                    _retry = true;
                    Dispatcher.BeginInvoke(() => { _retry = false; InvalidateVisual(); }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
            }
        }

        private bool _retry;

        private void Draw(DrawingContext dc)
        {
            int lines = _box.LineCount;
            int first = _box.GetFirstVisibleLineIndex(), last = _box.GetLastVisibleLineIndex();
            if (first < 0 || last < 0) return;
            first = Math.Clamp(first, 0, lines - 1);
            last = Math.Clamp(last, first, lines - 1);
            var text = _box.Text;
            int start = Math.Clamp(_box.GetCharacterIndexFromLineIndex(first), 0, text.Length);
            int end = last + 1 < lines ? Math.Clamp(_box.GetCharacterIndexFromLineIndex(last + 1), start, text.Length) : text.Length;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _box.ActualWidth, _box.ActualHeight)));
            try
            {
                for (int i = text.IndexOf(Query, start, Comparison); i >= 0 && i < end; i = text.IndexOf(Query, i + Query.Length, Comparison))
                {
                    bool current = i == _box.SelectionStart && _box.SelectionLength == Query.Length;
                    var a = _box.GetRectFromCharacterIndex(i);
                    var b = _box.GetRectFromCharacterIndex(Math.Min(text.Length, i + Query.Length));
                    if (a.IsEmpty || b.IsEmpty) continue;
                    double right = Math.Abs(a.Top - b.Top) < 1 ? b.Left : a.Left + 6;
                    dc.DrawRectangle(current ? Current : All, null, new Rect(new Point(a.Left, a.Top), new Point(Math.Max(right, a.Left + 2), a.Bottom)));
                }
            }
            finally { dc.Pop(); }
        }
    }
}
