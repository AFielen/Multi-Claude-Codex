using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiClaude;

public partial class MainWindow
{
    private Point dragStart;
    private Profile? dragProfile;
    private List<Profile>? renderedSidebar;
    private readonly Dictionary<string, (TextBlock Dot, TextBlock Caption, TextBlock Resources)> rowIndicators = [];
    private void OnResize(object sender, SizeChangedEventArgs e)
    {
        var newWide = ActualWidth >= 720;
        if (SideColumn == null || wide == newWide) return;
        wide = newWide;
        RenderPicker();
        UpdateUsageDisplay();
    }
    private void RenderPicker()
    {
        rendering = true;
        try
        {
            SideColumn.Width = new GridLength(wide && profiles.Count > 0 ? 178 : 0);
            Sidebar.Visibility = ShowIf(wide && profiles.Count > 0);
            CompactPicker.Visibility = ShowIf(!wide && profiles.Count > 0);
            QuickProfiles.Children.Clear();
            if (wide)
            {
                if (!ReferenceEquals(renderedSidebar, profiles))
                {
                    ProfileList.Items.Clear();
                    rowIndicators.Clear();
                    foreach (var profile in profiles)
                    {
                        var row = new StackPanel();
                        var layout = new DockPanel();
                        var badge = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                        badge.Children.Add(new System.Windows.Shapes.Ellipse { Fill = ProfileColor(profile) });
                        var portrait = TryImageBrush(catalog.PhotoFile(profile.Photo));
                        if (portrait != null) badge.Children.Add(new System.Windows.Shapes.Ellipse { Fill = portrait });
                        else if (profile.Name.Length > 0) badge.Children.Add(new TextBlock
                        {
                            Text = System.Globalization.StringInfo.GetNextTextElement(profile.Name).ToUpper(),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.SemiBold
                        });
                        layout.Children.Add(badge);
                        layout.Children.Add(row);
                        var top = new DockPanel();
                        var dot = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(dot, Dock.Right);
                        top.Children.Add(dot);
                        top.Children.Add(new TextBlock { Text = profile.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold });
                        var caption = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
                        row.Children.Add(top); row.Children.Add(caption);
                        var resources = new TextBlock { Style = (Style)FindResource("Caption"), Visibility = Visibility.Collapsed };
                        var card = new StackPanel();
                        card.Children.Add(layout);
                        card.Children.Add(resources);
                        rowIndicators.Add(profile.CodeKey, (dot, caption, resources));
                        var item = new ListBoxItem { Tag = profile, Content = card, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                        AutomationProperties.SetName(item, profile.Name);
                        ProfileList.Items.Add(item);
                    }
                    renderedSidebar = profiles;
                }
                ProfileList.SelectedIndex = selected == null ? -1 : profiles.IndexOf(selected);
                UpdateRowIndicators();
            }
            else
            {
                var direct = profiles.Take(2).ToList();
                if (direct.Count == 2 && selected != null && direct.All(p => p.Id != selected.Id)) direct[1] = selected;
                foreach (var profile in direct)
                {
                    var button = new Button
                    {
                        Content = PickerLabel(profile),
                        Padding = new Thickness(4, 2, 4, 2),
                        ToolTip = profile.Name,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 2, 0)
                    };
                    if (profile.Id == selected?.Id) button.BorderBrush = ProfileColor(profile);
                    AutomationProperties.SetAutomationId(button, "QuickProfile_" + profile.Id);
                    AutomationProperties.SetName(button, profile.Name);
                    button.Click += (_, _) => Safe(() => SetSelection(profile));
                    QuickProfiles.Children.Add(button);
                }
            }
        }
        finally { rendering = false; }
    }
    private DockPanel PickerLabel(Profile p)
    {
        var panel = new DockPanel();
        panel.Children.Add(new TextBlock { Text = "●", Foreground = ProfileColor(p), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = p.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = selected?.Id == p.Id ? FontWeights.SemiBold : FontWeights.Normal
        });
        return panel;
    }
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Safe(Search); }
    }
    private void SidebarSelect(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && ProfileList.SelectedItem is ListBoxItem { Tag: Profile profile })
            Safe(() => { SetSelection(profile); (ProfileList.SelectedItem as ListBoxItem)?.Focus(); });
    }
    private void SidebarDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProfileAt(e.OriginalSource) is { } p) Safe(() => ActivateById(p.Id));
    }
    private Profile? ProfileAt(object source) => source is DependencyObject element &&
        ItemsControl.ContainerFromElement(ProfileList, element) is ListBoxItem { Tag: Profile p } ? p : null;
    private void SidebarMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(ProfileList);
        dragProfile = ProfileAt(e.OriginalSource);
    }
    private void SidebarMouseMove(object sender, MouseEventArgs e)
    {
        var offset = e.GetPosition(ProfileList) - dragStart;
        if (dragProfile == null || e.LeftButton != MouseButtonState.Pressed ||
            Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var id = dragProfile.Id;
        dragProfile = null;
        DragDrop.DoDragDrop(ProfileList, new DataObject("MultiClaude.ProfileId", id), DragDropEffects.Move);
    }
    private void SidebarDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("MultiClaude.ProfileId") is long source && ProfileAt(e.OriginalSource) is { } target)
            Safe(() => { catalog.Move(source, target.Id); Reload(); });
    }
    private void SidebarKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        { e.Handled = true; Safe(() => MoveSelected(key == Key.Up ? -1 : 1)); }
        else if (key == Key.Enter) { e.Handled = true; Safe(OpenSelected); }
    }
    private void MoveSelected(int delta)
    {
        if (selected == null) return;
        var index = profiles.FindIndex(p => p.Id == selected.Id) + delta;
        if (index >= 0 && index < profiles.Count) { catalog.Move(selected.Id, profiles[index].Id); Reload(); }
    }
    private void RestoreGeometry()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var workArea = SystemParameters.WorkArea;
        var m = Regex.Match(catalog.Get("geometria") ?? "", @"^(\d{1,6})x(\d{1,6})(?:([+\-]-?\d{1,7})([+\-]-?\d{1,7}))?$");
        if (m.Success)
        {
            Width = Math.Clamp(double.Parse(m.Groups[1].Value) / dpi.DpiScaleX, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
            Height = Math.Clamp(double.Parse(m.Groups[2].Value) / dpi.DpiScaleY, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
            if (m.Groups[3].Success)
            {
                double Coordinate(string s, double scale, double far, double length) => s[0] == '+'
                    ? int.Parse(s.Substring(1)) / scale : far - length + int.Parse(s) / scale;
                var x = Coordinate(m.Groups[3].Value, dpi.DpiScaleX, workArea.Right, Width);
                var y = Coordinate(m.Groups[4].Value, dpi.DpiScaleY, workArea.Bottom, Height);
                var rect = new System.Drawing.Rectangle((int)(x * dpi.DpiScaleX), (int)(y * dpi.DpiScaleY), (int)(Width * dpi.DpiScaleX), (int)(Height * dpi.DpiScaleY));
                var monitor = System.Windows.Forms.Screen.FromRectangle(rect).WorkingArea;
                workArea = new Rect(monitor.X / dpi.DpiScaleX, monitor.Y / dpi.DpiScaleY, monitor.Width / dpi.DpiScaleX, monitor.Height / dpi.DpiScaleY);
                Left = x; Top = y;
            }
        }
        if (catalog.Get("compact_workspace") == null) { Width = Math.Min(480, Width); Height = Math.Min(360, Height); }
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        Left = Math.Clamp(double.IsNaN(Left) ? workArea.Left + 40 : Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(double.IsNaN(Top) ? workArea.Top + 40 : Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        catalog.Set("compact_workspace", "1");
    }
    private void SaveGeometry()
    {
        var rect = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (rect.IsEmpty || !double.IsFinite(rect.X) || !double.IsFinite(rect.Width)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        try { catalog.Set("geometria", FormattableString.Invariant($"{Math.Round(rect.Width * dpi.DpiScaleX)}x{Math.Round(rect.Height * dpi.DpiScaleY)}+{Math.Round(rect.X * dpi.DpiScaleX)}+{Math.Round(rect.Y * dpi.DpiScaleY)}")); }
        catch (Exception e) { MessageBox.Show(e.Message, T("erro")); }
    }
}
