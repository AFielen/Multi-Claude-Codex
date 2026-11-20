using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiClaude;

public partial class MainWindow
{
    private static readonly string[] Palette = ["#d97757", "#6a9bd8", "#5fa85e", "#b77fd0", "#d39b33", "#4fb0a8", "#d86a8f"];

    private void Edit(Profile? existing)
    {
        var draft = existing ?? catalog.Draft();
        var dialog = new Form(this, T(existing == null ? "novo" : "edit_profile"), 420, 505);
        Note(dialog.Fields, T("provider"), true);
        var provider = new ComboBox
        {
            ItemsSource = Enum.GetValues<ProfileProvider>(),
            SelectedItem = draft.Provider,
            IsEnabled = existing == null,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 150
        };
        Identify(provider, "ProfileProvider", T("provider"));
        dialog.Fields.Children.Add(provider);
        Note(dialog.Fields, T("name"), true);
        var name = new TextBox { Text = draft.Name, Padding = new Thickness(4, 3, 4, 3) };
        Identify(name, "ProfileName", T("name"));
        dialog.Fields.Children.Add(name);
        string? photo = draft.Photo;
        var preview = new System.Windows.Shapes.Ellipse { Width = 35, Height = 35, Fill = TryImageBrush(catalog.PhotoFile(photo)), Margin = new Thickness(0, 0, 7, 0) };
        var photoRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        photoRow.Children.Add(preview);
        photoRow.Children.Add(MakeButton(T("foto"), () =>
        {
            var chosen = Gallery(dialog, name.Text, photo);
            photo = chosen;
            preview.Fill = TryImageBrush(catalog.PhotoFile(photo));
        }, "ChoosePhoto"));
        dialog.Fields.Children.Add(photoRow);

        var color = draft.Color;
        Note(dialog.Fields, T("color"), true);
        var palette = new WrapPanel();
        dialog.Fields.Children.Add(palette);
        var contrast = Note(dialog.Fields, T("low_contrast"));
        void Swatches(bool focus = false)
        {
            palette.Children.Clear();
            foreach (var swatch in Palette.Append(color).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var rgb = (Color)ColorConverter.ConvertFromString(swatch);
                var active = string.Equals(swatch, color, StringComparison.OrdinalIgnoreCase);
                var button = new Button
                {
                    Background = new SolidColorBrush(rgb),
                    Width = 28,
                    Height = 25,
                    Margin = new Thickness(0, 0, 4, 2),
                    Content = active ? "✓" : "",
                    ToolTip = swatch,
                    Padding = new Thickness(0),
                    Foreground = Luminance(rgb) > .18 ? Brushes.Black : Brushes.White
                };
                Identify(button, "Swatch_" + swatch[1..], T("color") + " " + swatch);
                button.Click += (_, _) => { color = swatch; Swatches(true); };
                button.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Left or Key.Right)) return;
                    var choices = Palette.Append(color).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var index = choices.FindIndex(c => c.Equals(swatch, StringComparison.OrdinalIgnoreCase));
                    color = choices[(index + (e.Key == Key.Right ? 1 : -1) + choices.Count) % choices.Count];
                    e.Handled = true;
                    Swatches(true);
                };
                palette.Children.Add(button);
                if (focus && active) button.Focus();
            }
            var background = ((SolidColorBrush)FindResource("Bg")).Color;
            var a = Luminance((Color)ColorConverter.ConvertFromString(color)); var b = Luminance(background);
            contrast.Visibility = ShowIf((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) < 1.8);
            palette.Children.Add(MakeButton(T("custom_color"), () =>
            {
                using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(color) };
                if (picker.ShowDialog(new NativeOwner(dialog)) == System.Windows.Forms.DialogResult.OK)
                {
                    color = $"#{picker.Color.R:x2}{picker.Color.G:x2}{picker.Color.B:x2}";
                    Swatches();
                }
            }));
        }
        Swatches();
        Note(dialog.Fields, T("hotkey"), true);
        var shortcut = new TextBox { Text = draft.Shortcut, Padding = new Thickness(4, 2, 4, 2) };
        Identify(shortcut, "ShortcutKeys", T("hotkey"));
        dialog.Fields.Children.Add(shortcut);
        Note(dialog.Fields, T("hotkey_hint"));
        CheckBox Toggle(Panel panel, string key, bool value)
        {
            var box = new CheckBox { Content = T(key), IsChecked = value, Margin = new Thickness(0, 3, 0, 2) };
            Identify(box, "Option_" + key, T(key));
            panel.Children.Add(box);
            return box;
        }
        var border = Toggle(dialog.Fields, "border", draft.Border);
        border.IsEnabled = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        var tray = Toggle(dialog.Fields, "tray", draft.Tray);
        var claudeOptions = new StackPanel();
        dialog.Fields.Children.Add(claudeOptions);
        Note(claudeOptions, T("code_settings"), true);
        var isolated = Toggle(claudeOptions, "isolated", draft.Isolated);
        var share = Toggle(claudeOptions, "share_on", draft.Sharing);
        share.IsEnabled = draft.Isolated;
        isolated.Checked += (_, _) => share.IsEnabled = true;
        isolated.Unchecked += (_, _) => share.IsEnabled = false;
        Note(claudeOptions, T("share_items"));
        Note(claudeOptions, T("next_launch"));
        var codexNote = Note(dialog.Fields, T("codex_account"));
        void ShowProviderOptions()
        {
            bool claude = provider.SelectedItem is ProfileProvider.Claude;
            claudeOptions.Visibility = ShowIf(claude);
            codexNote.Visibility = ShowIf(!claude);
        }
        ShowProviderOptions();
        provider.SelectionChanged += (_, _) => ShowProviderOptions();

        string? savedKey = null;
        void Save()
        {
            dialog.Warning.Text = "";
            if (string.IsNullOrWhiteSpace(name.Text))
            { dialog.Warning.Text = T("novo_pergunta").TrimEnd(':', '：'); name.Focus(); return; }
            if (profiles.Any(p => p.Id != draft.Id && p.Name.Equals(name.Text.Trim(), StringComparison.Ordinal)))
            { dialog.Warning.Text = T("existe"); name.Focus(); return; }
            string keys;
            try { keys = ProfileBindings.NormalizeShortcut(shortcut.Text); }
            catch (Exception) { dialog.Warning.Text = T("hotkey_hint"); shortcut.Focus(); return; }
            if (keys.Length > 0 && profiles.Any(p => p.Id != draft.Id && p.Shortcut.Equals(keys, StringComparison.OrdinalIgnoreCase)))
            { dialog.Warning.Text = T("hotkey_busy"); shortcut.Focus(); return; }
            var selectedProvider = (ProfileProvider)provider.SelectedItem;
            var edited = draft with
            {
                Name = name.Text.Trim(),
                Provider = selectedProvider,
                Photo = photo,
                Color = color,
                Shortcut = keys,
                Border = border.IsChecked == true,
                Tray = tray.IsChecked == true,
                Isolated = selectedProvider == ProfileProvider.Codex || isolated.IsChecked == true,
                Sharing = selectedProvider == ProfileProvider.Claude && isolated.IsChecked == true && share.IsChecked == true
            };
            Profile? assigned = null;
            bool shareChanged = false;
            try
            {
                var saved = catalog.Save(edited, p =>
                {
                    if (existing == null || draft.Shortcut != keys)
                    {
                        bindings?.SetHotkey(p.Id, keys);
                        assigned = p;
                    }
                    if (p.Sharing != (existing?.Sharing ?? false))
                    {
                        integration.SetSharing(p, p.Sharing);
                        shareChanged = true;
                    }
                });
                savedKey = saved.CodeKey;
                dialog.DialogResult = true;
            }
            catch (Exception error)
            {
                var reason = error.Message;
                if (assigned != null)
                    try { bindings?.SetHotkey(assigned.Id, existing?.Shortcut ?? ""); }
                    catch (Exception restore) { reason += "\n" + restore.Message; }
                if (shareChanged)
                    try { integration.SetSharing(edited, existing?.Sharing == true); }
                    catch (Exception restore) { reason += "\n" + restore.Message; }
                dialog.Warning.Text = reason;
            }
        }
        dialog.Action(T("cancelar"), dialog.Close);
        dialog.Action(T("save"), Save, true, "SaveProfile");
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save(); } };
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        if (dialog.ShowDialog() == true) Reload(savedKey);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double n = channel / 255.0;
            return n <= 0.04045 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private sealed class NativeOwner(Window window) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle => new System.Windows.Interop.WindowInteropHelper(window).Handle;
    }

    private string? Gallery(Window owner, string label, string? current)
    {
        var dialog = new Form(owner, Format("fotos_titulo", "n", label), 405, 370);
        Note(dialog.Fields, T("fotos_dica"));
        var tiles = new WrapPanel();
        dialog.Fields.Children.Add(tiles);
        string? selection = current;
        void Select(string? file) { selection = file; dialog.DialogResult = true; }
        foreach (var photo in catalog.Photos())
        {
            var brush = TryImageBrush(catalog.PhotoFile(photo));
            if (brush == null) continue;
            var button = new Button
            {
                Width = 57,
                Height = 57,
                Margin = new Thickness(2),
                ToolTip = photo,
                Content = new System.Windows.Shapes.Ellipse { Width = 42, Height = 42, Fill = brush }
            };
            Identify(button, "Photo_" + Path.GetFileNameWithoutExtension(photo), photo);
            button.Click += (_, _) => Select(photo);
            tiles.Children.Add(button);
        }
        dialog.Action(T("sem_foto"), () => Select(null));
        dialog.Action(T("abrir_fotos"), () =>
        {
            try
            {
                var directory = Path.Combine(catalog.Home, "profile_pictures");
                StoragePaths.RequireOrdinary(directory);
                Directory.CreateDirectory(directory);
                OpenInShell(directory);
            }
            catch (Exception error) { Error(dialog, error); }
        });
        dialog.Action(T("adicionar_img"), () =>
        {
            var picker = new OpenFileDialog { Title = T("adicionar_img"), Filter = T("imagens") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.ico;*.webp;*.tif;*.tiff" };
            if (picker.ShowDialog(dialog) != true) return;
            try { Select(catalog.ImportPhoto(picker.FileName)); }
            catch (Exception error) { Error(dialog, error); }
        });
        dialog.ShowDialog();
        return selection;
    }
}
