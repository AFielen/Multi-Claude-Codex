using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiClaude;

public partial class MainWindow
{
    private sealed class Form : Window
    {
        internal StackPanel Fields { get; } = new();
        internal WrapPanel Actions { get; } = new() { HorizontalAlignment = HorizontalAlignment.Right };
        internal TextBlock Warning { get; } = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Margin = new Thickness(0, 2, 0, 3) };
        internal Form(Window owner, string title, double width, double height)
        {
            Owner = owner; Title = title;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Width = width; Height = height;
            MinWidth = Math.Min(width, 290); MinHeight = Math.Min(height, 180);
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width);
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
            SetResourceReference(BackgroundProperty, "Bg");
            SetResourceReference(ForegroundProperty, "Text");
            var panel = new DockPanel { Margin = new Thickness(14, 9, 14, 10) };
            var footer = new StackPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            footer.Children.Add(Warning); footer.Children.Add(Actions);
            panel.Children.Add(footer);
            panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = Fields, Padding = new Thickness(1, 0, 4, 0) });
            Content = panel;
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        }
        internal Button Action(string name, Action run, bool primary = false, string? id = null)
        {
            var button = MakeButton(name, run, id);
            if (primary)
            {
                button.SetResourceReference(BackgroundProperty, "Accent");
                button.Foreground = Brushes.White;
                button.IsDefault = true;
            }
            Actions.Children.Add(button);
            return button;
        }
    }

    private static Button MakeButton(string name, Action run, string? id = null)
    {
        var button = new Button { Content = name, Margin = new Thickness(0, 0, 4, 3), Padding = new Thickness(8, 3, 8, 3), MinHeight = 24 };
        if (id != null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => run();
        return button;
    }
    private static TextBlock Note(Panel parent, string text, bool heading = false)
    {
        var note = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = heading ? 14 : 12,
            Margin = heading ? new Thickness(0, 8, 0, 4) : new Thickness(0, 1, 0, 4)
        };
        if (!heading) note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        parent.Children.Add(note);
        return note;
    }
    private static void Identify(FrameworkElement control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
    }
    private void Search()
    {
        if (profiles.Count == 0) return;
        var dialog = new Form(this, T("switch_profile"), 405, 300);
        Note(dialog.Fields, T("search_profiles"), true);
        var query = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
        Identify(query, "ProfileSearch", T("search_profiles"));
        dialog.Fields.Children.Add(query);
        var results = new ListBox { MaxHeight = 145, Margin = new Thickness(0, 5, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Identify(results, "ProfileResults", T("profiles"));
        dialog.Fields.Children.Add(results);
        var empty = Note(dialog.Fields, T("no_match"));
        Profile? chosen = null;
        void Pick() { if (results.SelectedItem is ListBoxItem { Tag: Profile p }) { chosen = p; dialog.DialogResult = true; } }
        dialog.Action(T("cancelar"), dialog.Close);
        var open = dialog.Action(T("abrir"), Pick, true, "SearchOpen");
        void Filter()
        {
            var old = ((results.SelectedItem as ListBoxItem)?.Tag as Profile)?.Id ?? selected?.Id;
            var terms = Fold(query.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = profiles.Where(p => terms.All(word => Fold(p.Name).Contains(word, StringComparison.Ordinal))).ToList();
            results.Items.Clear();
            foreach (var p in matches)
            {
                var row = new DockPanel();
                var indicator = new TextBlock { FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
                DockPanel.SetDock(indicator, Dock.Right);
                row.Children.Add(indicator);
                row.Children.Add(PickerLabel(p));
                var item = new ListBoxItem { Content = row, Tag = p, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(item, p.Name);
                results.Items.Add(item);
                if (p.Id == old) results.SelectedItem = item;
            }
            if (results.SelectedIndex < 0 && matches.Count > 0) results.SelectedIndex = 0;
            RefreshMarkers();
            if (results.SelectedItem != null) results.ScrollIntoView(results.SelectedItem);
            empty.Visibility = ShowIf(matches.Count == 0);
            open.IsEnabled = matches.Count > 0;
        }
        void RefreshMarkers()
        {
            foreach (ListBoxItem item in results.Items)
            {
                var marker = (TextBlock)((DockPanel)item.Content).Children[0];
                var state = Indicator((Profile)item.Tag);
                marker.Text = state.Mark + "  " + state.Caption;
                marker.Foreground = state.Ink;
            }
        }
        query.TextChanged += (_, _) => Filter();
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (!(query.IsKeyboardFocusWithin || results.IsKeyboardFocusWithin)) return;
            if (e.Key is Key.Up or Key.Down && results.Items.Count > 0)
            {
                results.SelectedIndex = Math.Clamp(results.SelectedIndex + (e.Key == Key.Up ? -1 : 1), 0, results.Items.Count - 1);
                results.ScrollIntoView(results.SelectedItem);
                e.Handled = true;
            }
            if (e.Key == Key.Enter) { e.Handled = true; Pick(); }
        };
        results.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(results, element) is ListBoxItem) Pick();
        };
        Filter();
        dialog.Loaded += (_, _) => query.Focus();
        IndicatorsChanged += RefreshMarkers;
        try { dialog.ShowDialog(); } finally { IndicatorsChanged -= RefreshMarkers; }
        if (chosen != null) ActivateById(chosen.Id);
    }

    private static string Fold(string value)
    {
        var chars = value.ToLowerInvariant().Normalize(NormalizationForm.FormKD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Replace("ß", "ss", StringComparison.Ordinal);
    }
}
