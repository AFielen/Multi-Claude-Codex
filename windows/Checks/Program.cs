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
            CodeProcessRegistryChecks.Run(temp.FullName);
            Console.WriteLine("PASS managed Claude Code process registrations");
            ResourceChecks.Run();
            Console.WriteLine("PASS process tree attribution, Windows CPU and working set sampling");
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
        int samplePhase = 0;
        Dictionary<string, ResourceReading> ReadSamples(IReadOnlyList<Profile> profiles)
        {
            if (app.Dispatcher.CheckAccess() || profiles.Count != 2)
                throw new InvalidOperationException("Process counters must be read in a background batch.");
            return Volatile.Read(ref samplePhase) switch
            {
                0 => [],
                1 => new() { [first.CodeKey] = new(0, 512L << 20), [second.CodeKey] = new(null, 1536L << 20) },
                2 => throw new IOException("Simulated sampling failure"),
                _ => new() { [second.CodeKey] = new(12.5, null) }
            };
        }
        var window = new MainWindow(catalog, ReadSamples);
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
            if (Node<TextBlock>("ProfileResources") is not { Visibility: Visibility.Collapsed })
                throw new Exception("A compact profile needs a resource readout that hides when no instance is running.");
            if (window.Width != 480 || window.Height != 360 || Node<TextBlock>("SelectedName").Text != second.Name)
                throw new Exception("Default dimensions or selected profile changed.");
            var resourceText = Node<TextBlock>("ProfileResources");
            Volatile.Write(ref samplePhase, 1);
            string secondUsage = $"CPU — · RAM {1.5} GiB";
            PumpUntil(() => resourceText.IsVisible && resourceText.Text == secondUsage);
            if (!Node<TextBlock>("DesktopStatus").Text.Contains("Claude Desktop"))
                throw new Exception("A running Code session must distinguish a closed Desktop from the profile's local activity.");
            window.UpdateLayout();
            if (resourceText.ActualHeight <= 0 || resourceText.ActualWidth < resourceText.DesiredSize.Width)
                throw new Exception("Compact profile clipped the resource reading.");
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
            IEnumerable<TextBlock> RowTexts(int index) =>
                DialogChecks.VisualChildren((ListBoxItem)list.Items[index]).OfType<TextBlock>();
            const string firstUsage = "CPU 0 % · RAM 512 MiB";
            var firstReading = RowTexts(0).Single(t => t.IsVisible && t.Text == firstUsage);
            var secondReading = RowTexts(1).Single(t => t.IsVisible && t.Text == secondUsage);
            if (!RowTexts(1).Any(t => t.Text.Contains("89 %")))
                throw new Exception("Resource reading displaced the quota warning in the sidebar.");
            list.SelectedIndex = 0;
            PumpUntil(() => Node<TextBlock>("SelectedName").Text == first.Name);
            if (resourceText.Text != firstUsage) throw new Exception("Selection retained the previous profile's counters.");
            Volatile.Write(ref samplePhase, 2);
            PumpUntil(() => !resourceText.IsVisible && !firstReading.IsVisible && !secondReading.IsVisible);
            if (!Node<TextBlock>("DesktopStatus").Text.Contains(Texts.Get("de", "fechado")) ||
                !RowTexts(1).Any(t => t.Text.Contains("89 %")))
                throw new Exception("Failed resource sample displaced desktop or quota status.");
            Volatile.Write(ref samplePhase, 3);
            PumpUntil(() => secondReading.IsVisible && secondReading.Text == $"CPU {12.5} % · RAM —");
            if (firstReading.IsVisible || resourceText.IsVisible || list.SelectedIndex != 0)
                throw new Exception("New resource sample changed selection or left stale counters for a closed profile.");
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
