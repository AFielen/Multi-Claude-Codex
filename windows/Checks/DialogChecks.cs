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
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.TryPop(out var current))
        {
            if (current is T item && AutomationProperties.GetAutomationId(item) == id) return item;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                stack.Push(VisualTreeHelper.GetChild(current, i));
        }
        throw new Exception("Missing dialog control " + id);
    }
}
