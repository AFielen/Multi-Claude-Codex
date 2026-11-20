using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MultiClaude;

public partial class MainWindow
{
    public static Uri OriginalAuthorUri { get; } = new("https://github.com/RichardKPL");
    public static Uri CodexRepositoryUri { get; } = new("https://github.com/AFielen/Multi-Claude-Codex");

    private static readonly (string Key, string Name)[] Languages =
    [
        ("pt", "Português (BR)"), ("en", "English"), ("es", "Español"), ("fr", "Français"),
        ("de", "Deutsch"), ("it", "Italiano"), ("ru", "Русский"), ("ja", "日本語"), ("zh", "中文"), ("ko", "한국어")
    ];

    private ComboBox LanguagePicker(Panel panel)
    {
        var combo = new ComboBox { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left };
        Identify(combo, "Language", T("idioma"));
        foreach (var (key, name) in Languages)
        {
            var item = new ComboBoxItem { Content = name, Tag = key };
            combo.Items.Add(item);
            if (key == language) combo.SelectedItem = item;
        }
        if (combo.SelectedIndex < 0) combo.SelectedIndex = 1;
        panel.Children.Add(combo);
        return combo;
    }

    private void Welcome()
    {
        var dialog = new Form(this, T("bv_titulo"), 390, 238);
        var title = Note(dialog.Fields, T("bv_titulo"), true);
        var description = Note(dialog.Fields, T("bv_sub"));
        var chooser = LanguagePicker(dialog.Fields);
        var import = new CheckBox { Content = T("importar"), Margin = new Thickness(0, 8, 0, 2), IsChecked = false };
        dialog.Fields.Children.Add(import);
        var next = dialog.Action(T("continuar"), () => dialog.DialogResult = true, true, "WelcomeContinue");
        chooser.SelectionChanged += (_, _) =>
        {
            string proposed = (string)((ComboBoxItem)chooser.SelectedItem).Tag;
            title.Text = dialog.Title = Texts.Get(proposed, "bv_titulo");
            description.Text = Texts.Get(proposed, "bv_sub");
            import.Content = Texts.Get(proposed, "importar");
            next.Content = Texts.Get(proposed, "continuar");
        };
        var accepted = dialog.ShowDialog() == true;
        var choice = (string)((ComboBoxItem)chooser.SelectedItem).Tag;
        language = choice;
        catalog.Set("idioma", language);
        DataContext = new Captions(language);
        Reload();
        if (accepted && import.IsChecked == true) Import();
    }

    private void Settings()
    {
        var dialog = new Form(this, T("config"), 415, 475);
        Note(dialog.Fields, T("aparencia"), true);
        var themes = new ComboBox { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left };
        Identify(themes, "Theme", T("aparencia"));
        string appliedTheme = catalog.Get("tema") ?? "sistema";
        foreach (var key in new[] { "sistema", "claro", "escuro" })
        {
            var option = new ComboBoxItem { Content = T(key), Tag = key };
            themes.Items.Add(option);
            if (key == appliedTheme) themes.SelectedItem = option;
        }
        if (themes.SelectedIndex < 0) themes.SelectedIndex = 0;
        dialog.Fields.Children.Add(themes);
        Note(dialog.Fields, T("idioma"), true);
        var chooser = LanguagePicker(dialog.Fields);
        Note(dialog.Fields, T("dados_sec"), true);
        Action? after = null;
        void Finish(Action? action)
        {
            try
            {
                catalog.Set("tema", (string)((ComboBoxItem)themes.SelectedItem).Tag);
                language = (string)((ComboBoxItem)chooser.SelectedItem).Tag;
                catalog.Set("idioma", language);
                Theme.Apply(catalog.Get("tema") ?? "sistema");
                DataContext = new Captions(language);
                after = action;
                dialog.DialogResult = true;
            }
            catch (Exception error) { Error(dialog, error); }
        }
        var imports = MakeButton(T("importar"), () => Finish(Import), "ImportProfiles");
        imports.HorizontalAlignment = HorizontalAlignment.Left;
        dialog.Fields.Children.Add(imports);
        Note(dialog.Fields, T("sobre"), true);
        Note(dialog.Fields, "Multi Claude Codex 2.3.0 · WPF");
        var author = Note(dialog.Fields, "");
        AddCredit("Multi Claude Codex · © 2026 Axel (AFielen)", CodexRepositoryUri);
        author.Inlines.Add(new LineBreak());
        AddCredit("Based on Multi Claude, created by Richard KPL", OriginalAuthorUri);
        void AddCredit(string label, Uri target)
        {
            var link = new Hyperlink(new Run(label)) { NavigateUri = target, ToolTip = target.AbsoluteUri };
            link.RequestNavigate += OpenCreditLink;
            author.Inlines.Add(link);
        }
        Note(dialog.Fields, "© 2026 Richard KPL · GPL-3.0\n" + T("direitos") + "\n" + T("nao_oficial"));
        var links = new WrapPanel();
        links.Children.Add(MakeButton(T("codigo"), () => Safe(() => OpenInShell(CodexRepositoryUri.AbsoluteUri))));
        links.Children.Add(MakeButton(T("licenca"), () => Safe(() => OpenInShell(CodexRepositoryUri.AbsoluteUri + "/blob/main/LICENSE"))));
        dialog.Fields.Children.Add(links);
        Note(dialog.Fields, T("perigo"), true);
        Note(dialog.Fields, T("apagar_tudo_sub"));
        var reset = MakeButton(T("apagar_tudo"), () => Finish(Reset), "ResetData");
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        dialog.Fields.Children.Add(reset);
        dialog.Action(T("cancelar"), dialog.Close);
        dialog.Action(T("save"), () => Finish(null), true, "SaveSettings");
        if (dialog.ShowDialog() == true) { Reload(); after?.Invoke(); }
    }

    private void Reset()
    {
        var dialog = new Form(this, T("apagar_tudo"), 415, 320);
        Note(dialog.Fields, T("apagar_tudo_sub"), true);
        var notices = new List<string> { T("pastas_preservadas") };
        if (profiles.Any(p => p.Provider == ProfileProvider.Claude)) notices.Add(T("hold_code"));
        if (profiles.Any(p => p.Provider == ProfileProvider.Codex)) notices.Add(T("hold_codex"));
        Note(dialog.Fields, string.Join("\n", notices));
        string word = T("palavra");
        Note(dialog.Fields, Format("apagar_confirma", "p", word));
        var field = new TextBox();
        Identify(field, "ResetConfirmation", T("apagar_tudo"));
        dialog.Fields.Children.Add(field);
        dialog.Action(T("cancelar"), dialog.Close);
        var erase = dialog.Action(T("apagar_tudo"), () => dialog.DialogResult = true, false);
        erase.IsEnabled = false;
        field.TextChanged += (_, _) => erase.IsEnabled = string.Equals(field.Text.Trim(), word, StringComparison.OrdinalIgnoreCase);
        if (dialog.ShowDialog() != true) return;
        EndLogin();
        catalog.Reset(integration.CloseDesktop);
        Theme.Apply("sistema");
        language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        DataContext = new Captions(language);
        Reload();
        Width = 480; Height = 360;
        Tabs.SelectedIndex = 0;
        Welcome();
    }

    private void Import()
    {
        var dialog = new Form(this, T("importar"), 505, 400);
        Note(dialog.Fields, T("importar_sub"), true);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices = new StackPanel();
        dialog.Fields.Children.Add(choices);
        var empty = Note(dialog.Fields, T("nenhum_encontrado"));
        var selectedFolders = new List<CheckBox>();
        void AddFolder(string path)
        {
            if (!paths.Add(path)) return;
            var option = new CheckBox
            {
                Content = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap },
                IsChecked = true,
                Tag = path,
                Margin = new Thickness(0, 3, 0, 3)
            };
            selectedFolders.Add(option);
            choices.Children.Add(option);
            empty.Visibility = Visibility.Collapsed;
        }
        foreach (var folder in catalog.Discover(integration.RunningFolders())) AddFolder(folder);
        int count = 0;
        void Attempt(Func<int> run)
        {
            try { count = run(); dialog.DialogResult = true; }
            catch (Exception error) { Error(dialog, error); }
        }
        var tools = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        tools.Children.Add(MakeButton(T("procurar_pasta"), () =>
        {
            var picker = new OpenFolderDialog { Title = T("procurar_pasta") };
            if (picker.ShowDialog(dialog) != true) return;
            try
            {
                if (catalog.Recognized(picker.FolderName)) AddFolder(picker.FolderName);
                else dialog.Warning.Text = T("pasta_invalida");
            }
            catch (Exception error) { Error(dialog, error); }
        }, "BrowseProfileFolder"));
        tools.Children.Add(MakeButton(T("procurar_db"), () =>
        {
            var picker = new OpenFileDialog { Title = T("procurar_db"), Filter = "SQLite|*.db|*.*|*.*" };
            if (picker.ShowDialog(dialog) == true) Attempt(() => catalog.ImportDatabase(picker.FileName));
        }, "BrowseProfileDatabase"));
        dialog.Fields.Children.Add(tools);
        dialog.Action(T("cancelar"), dialog.Close);
        dialog.Action(T("importar_sel"), () => Attempt(() => catalog.ImportFolders(selectedFolders.Where(c => c.IsChecked == true).Select(c => (string)c.Tag))), true, "ImportSelected");
        if (dialog.ShowDialog() == true)
        {
            Reload();
            Feedback.Text = Format("importados", "n", count);
        }
    }
}
