using MultiClaude;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Text.Json;

internal static class CheckRunner
{
    [STAThread]
    public static int Main()
    {
        var temp = Directory.CreateTempSubdirectory("multi-claude-wpf-checks-");
        try
        {
            foreach (string language in new[] { "pt", "en", "es", "fr", "de", "it", "ru", "ja", "zh", "ko" })
                if (Texts.Get(language, "overview") == "overview" || Texts.Get(language, "novo") == "novo")
                    throw new Exception("Missing language resources for " + language);
            CheckArguments(temp.FullName);
            StorageChecks.Run(temp.FullName);
            Console.WriteLine("PASS SQLite import, identities, path and deletion rules");
            IntegrationChecks.Run(Path.Combine(temp.FullName, "integration"));
            Console.WriteLine("PASS Windows integration, isolated launch settings, shortcuts, sharing");
            UsageChecks.Run(Path.Combine(temp.FullName, "usage"));
            Console.WriteLine("PASS transcript aggregation, current limits and custom status line preservation");
            UiTest(Path.Combine(temp.FullName, "window"));
            Console.WriteLine("PASS compact WPF, current readings, selection, focused control and scroll offset");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            if (string.Equals(Path.GetDirectoryName(temp.FullName), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                temp.Name.StartsWith("multi-claude-wpf-checks-", StringComparison.Ordinal)) temp.Delete(true);
        }
    }

    private static void CheckArguments(string home)
    {
        string key = new string('a', 32);
        var cmd = ManagerArguments.Parse(["--code-terminal", key, "--manager-home", home, "--login", "--console-pid", "123"], home);
        if (!cmd.Login || cmd.ConsolePid != 123 || cmd.Key != key || cmd.Home != home) throw new Exception("Copied command arguments lost.");
        foreach (string[] invalid in new string[][] { ["--open-profile", key], ["--code-terminal", "shared", "--login"], ["--open-profile", "wrong", "--manager-home", home] })
        {
            try { ManagerArguments.Parse(invalid, home); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid activation accepted.");
        }
    }

    private static void UiTest(string home)
    {
        var catalog = new Catalog(home);
        catalog.Set("idioma", "de");
        catalog.Set("tema", "claro");
        var first = catalog.Save(catalog.Draft() with { Name = "Testprofil A", Shortcut = "" });
        var second = catalog.Save(catalog.Draft() with { Name = "Testprofil B", Shortcut = "" });
        catalog.Set("selected_code_key", second.CodeKey);
        if (!Usage.SetCapture(second, true)) throw new Exception("Install fixture status line");
        string code = Path.Combine(home, "claude-code", second.CodeKey);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        Theme.Apply("claro");
        var window = new MainWindow(catalog);
        try
        {
            void Record(int percent) => File.WriteAllText(Path.Combine(code, "multiclaude-limits.json"), JsonSerializer.Serialize(new
            {
                observed_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                five_hour = new { used_percentage = percent, resets_at = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds() },
                seven_day = new { used_percentage = 24, resets_at = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds() }
            }));
            Record(62);
            window.Show();
            window.Activate();
            T Node<T>(string id) => (T)window.FindName(id);
            PumpUntil(() => Node<TextBlock>("FivePercent").Text.Contains("62"));
            if (window.Width != 480 || window.Height != 360 || Node<TextBlock>("SelectedName").Text != second.Name)
                throw new Exception("Default dimensions or selected profile changed.");
            if (Environment.GetEnvironmentVariable("MULTI_CLAUDE_TEST_SCREENSHOT") is string screenshot)
            {
                var capture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                capture.Render(window);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(capture));
                using var stream = File.Create(screenshot);
                png.Save(stream);
            }
            var scroll = Node<ScrollViewer>("OverviewScroll");
            var button = Node<Button>("RefreshOverview");
            // Make scrolling necessary at every test machine's font scale.
            window.Height = window.MinHeight;
            window.UpdateLayout();
            button.Focus();
            scroll.ScrollToVerticalOffset(Math.Min(12, scroll.ScrollableHeight));
            window.UpdateLayout();
            double offset = scroll.VerticalOffset;
            var focus = Keyboard.FocusedElement;
            if (offset <= 0 || focus != button) throw new Exception("Could not establish scroll/focus preconditions.");
            var tabs = Node<TabControl>("Tabs");
            var activeTab = tabs.SelectedItem;
            Record(89);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Node<TextBlock>("FivePercent").Text.Contains("89"));
            if (!ReferenceEquals(scroll, Node<ScrollViewer>("OverviewScroll")) || Math.Abs(scroll.VerticalOffset - offset) > .5 ||
                !ReferenceEquals(focus, Keyboard.FocusedElement) || !ReferenceEquals(activeTab, tabs.SelectedItem) ||
                Node<TextBlock>("SelectedName").Text != second.Name)
                throw new Exception("Refresh changed the selection, tab, focus or scroll position.");
            window.Width = 920;
            window.UpdateLayout();
            PumpUntil(() => Node<ListBox>("ProfileList").IsVisible);
            var list = Node<ListBox>("ProfileList");
            list.SelectedIndex = 0;
            PumpUntil(() => Node<TextBlock>("SelectedName").Text == first.Name);
            if (catalog.Get("selected_code_key") != first.CodeKey) throw new Exception("Selection not persisted.");
            DialogChecks.Run(window);
        }
        finally { window.Close(); app.Shutdown(); }
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (ready()) return;
            Thread.Sleep(25);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("WPF view did not reach its expected state.");
    }
}
