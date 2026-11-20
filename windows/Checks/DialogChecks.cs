using MultiClaude;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class DialogChecks
{
    public static void Run(MainWindow window)
    {
        var settings = (Button)window.FindName("SettingsButton");
        void ChangeLanguage(string currentLabel, string nextCode, string nextLabel)
        {
            WithDialog(window, settings, dialog =>
            {
                var picker = Find<ComboBox>(dialog, "Language");
                CheckVisibleLabel(currentLabel);
                picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, nextCode));
                CheckVisibleLabel(nextLabel);
                Click(Find<Button>(dialog, "SaveSettings"));

                void CheckVisibleLabel(string label)
                {
                    picker.UpdateLayout();
                    if (!VisualChildren(picker).OfType<TextBlock>().Any(t => t.IsVisible && t.ActualWidth > 0 && t.Text == label))
                        throw new Exception($"Language picker did not display '{label}' when collapsed.");
                }
            });
        }
        ChangeLanguage("Deutsch", "fr", "Français");
        ChangeLanguage("Français", "de", "Deutsch");
        Console.WriteLine("PASS settings display the selected language before and after saving");

        var selectedName = (TextBlock)window.FindName("SelectedName");
        string original = selectedName.Text;
        void SaveName(string next)
        {
            WithDialog(window, (Button)window.FindName("EditProfile"), dialog =>
            {
                var field = Find<TextBox>(dialog, "ProfileName");
                if (!field.IsKeyboardFocusWithin) throw new Exception("Editor did not focus the profile name.");
                field.Text = next;
                Click(Find<Button>(dialog, "SaveProfile"));
            });
            if (selectedName.Text != next) throw new Exception("Edited profile name was not shown.");
        }
        SaveName("Café Arbeit");
        window.Width = 480;
        window.UpdateLayout();
        WithDialog(window, (Button)window.FindName("SwitchProfile"), dialog =>
        {
            var query = Find<TextBox>(dialog, "ProfileSearch");
            var results = Find<ListBox>(dialog, "ProfileResults");
            if (!query.IsKeyboardFocusWithin) throw new Exception("Search did not focus the query.");
            query.Text = "ARBEIT cafe";
            if (results.Items.Count != 1 || results.SelectedItem is not ListBoxItem { Tag: Profile { Name: "Café Arbeit" } })
                throw new Exception("Accent-insensitive, unordered profile search failed.");
            query.Clear();
            results.SelectedIndex = 0;
            Key(dialog, System.Windows.Input.Key.Down);
            if (results.SelectedIndex != 1) throw new Exception("Down did not move to the next profile.");
            query.Text = "unmatched-search-term";
            if (results.Items.Count != 0 || Find<Button>(dialog, "SearchOpen").IsEnabled)
                throw new Exception("No-match search still allows activation.");
            Key(dialog, System.Windows.Input.Key.Escape);
        });
        if (selectedName.Text != "Café Arbeit") throw new Exception("Cancelled search changed the selected profile.");
        SaveName(original);
        Console.WriteLine("PASS WPF editor save and search keyboard focus, accents, arrows, cancel");
        CheckCodexProfiles(window);
    }

    private static void CheckCodexProfiles(MainWindow window)
    {
        T Node<T>(string id) => (T)window.FindName(id);
        WithDialog(window, Node<Button>("NewProfile"), dialog =>
        {
            var provider = Find<ComboBox>(dialog, "ProfileProvider");
            if (!provider.IsEnabled || provider.SelectedItem is not ProfileProvider.Claude)
                throw new Exception("New profiles must default to Claude and allow choosing Codex.");
            var isolated = Find<CheckBox>(dialog, "Option_isolated");
            var sharing = Find<CheckBox>(dialog, "Option_share_on");
            isolated.IsChecked = true;
            sharing.IsChecked = true;
            provider.SelectedItem = ProfileProvider.Codex;
            if (isolated.IsVisible || sharing.IsVisible)
                throw new Exception("Codex editor exposes Claude Code settings.");
            Find<TextBox>(dialog, "ProfileName").Text = "QA Work";
            Find<TextBox>(dialog, "ShortcutKeys").Clear();
            Click(Find<Button>(dialog, "SaveProfile"));
        });
        var codexTab = Node<TabItem>("CodeTab");
        if (Node<TextBlock>("SelectedName").Text != "QA Work" || !Equals(codexTab.Header, "Codex CLI") ||
            Node<TabItem>("OverviewTab").Visibility != Visibility.Collapsed ||
            Node<FrameworkElement>("CodeUsage").Visibility != Visibility.Collapsed ||
            Node<Button>("SignIn").Visibility != Visibility.Collapsed || Node<Button>("SignOut").Visibility != Visibility.Collapsed ||
            Node<Button>("CodeLogin").Visibility != Visibility.Visible)
            throw new Exception("Codex workspace mixed Claude account or usage actions into its desktop/CLI controls.");
        WithDialog(window, Node<Button>("EditProfile"), dialog =>
        {
            var provider = Find<ComboBox>(dialog, "ProfileProvider");
            if (provider.IsEnabled || provider.SelectedItem is not ProfileProvider.Codex)
                throw new Exception("Saved Codex profile did not retain its provider.");
        });
        WithDialog(window, Node<Button>("SwitchProfile"), dialog =>
        {
            Find<TextBox>(dialog, "ProfileSearch").Text = "codex work";
            var results = Find<ListBox>(dialog, "ProfileResults");
            if (results.Items.Count != 1 || results.SelectedItem is not ListBoxItem
                { Tag: Profile { Name: "QA Work", Provider: ProfileProvider.Codex, Isolated: true, Sharing: false } })
                throw new Exception("Provider search failed or saved Codex profile inherited Claude settings.");
        });
        window.Width = 920;
        window.UpdateLayout();
        var list = Node<ListBox>("ProfileList");
        list.SelectedItem = list.Items.Cast<ListBoxItem>().First(item => item.Tag is Profile { Provider: ProfileProvider.Claude });
        if (Node<TabItem>("OverviewTab").Visibility != Visibility.Visible || !Equals(codexTab.Header, "Claude Code") ||
            Node<FrameworkElement>("CodeUsage").Visibility != Visibility.Visible)
            throw new Exception("Switching back to Claude did not restore its profile controls.");
        Console.WriteLine("PASS mixed-provider WPF creation, editing, search and account-specific controls");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Key(Window dialog, Key key) =>
        dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static void WithDialog(MainWindow owner, Button trigger, Action<Window> check)
    {
        Exception? failure = null;
        bool visited = false;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var dialog = owner.OwnedWindows.OfType<Window>().SingleOrDefault(w => w.IsVisible);
            try
            {
                if (dialog == null) throw new Exception("Dialog did not open.");
                dialog.Activate();
                dialog.UpdateLayout();
                visited = true;
                check(dialog);
            }
            catch (Exception error) { failure = error; }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        });
        Click(trigger);
        if (failure != null) throw new Exception("Dialog check failed.", failure);
        if (!visited) throw new Exception("Dialog was not exercised.");
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement
        => VisualChildren(root).OfType<T>().FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == id)
            ?? throw new Exception("Missing dialog control " + id);

    internal static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.TryPop(out var current))
        {
            yield return current;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }
}
