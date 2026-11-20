using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using MultiClaude;

internal static class ManagerMenuChecks
{
    // Opening builds a snapshot, which also needs to work when the strip starts empty.
    private static void Refresh(Forms.ContextMenuStrip menu)
    {
        menu.Show(System.Drawing.Point.Empty);
        if (!menu.Visible) throw new Exception("The tray menu did not open.");
        menu.Close();
    }

    private static Forms.ToolStripMenuItem Entry(Forms.ContextMenuStrip menu, string label) =>
        menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == label);

    private static Forms.ToolStripMenuItem ProfileEntry(Forms.ContextMenuStrip menu, Profile profile) =>
        Entry(menu, $"{profile.Name.Replace("&", "&&")} · {profile.Provider}");

    private static void WaitForTrayStatus(Forms.ContextMenuStrip menu, Profile profile, bool running)
    {
        CheckRunner.PumpUntil(() =>
        {
            Refresh(menu);
            var items = ProfileEntry(menu, profile).DropDownItems;
            return items[0].Text == Texts.Get("de", running ? "switch" : "abrir") && items[1].Enabled == running;
        }, $"tray actions for {profile.Name} with its desktop {(running ? "running" : "closed")}");
    }

    private static void CheckModalCommands(Forms.ContextMenuStrip menu, Profile profile)
    {
        Refresh(menu);
        if (ProfileEntry(menu, profile).Enabled || Entry(menu, Texts.Get("de", "exit_manager")).Enabled ||
            !Entry(menu, Texts.Get("de", "show_manager")).Enabled)
            throw new Exception("A manager dialog must remain reachable without tray commands interrupting it.");
    }

    private sealed record NativeOwner(nint Handle) : Forms.IWin32Window;
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();

    private static void CheckNativeModal(MainWindow window, Profile profile)
    {
        using var dialog = new Forms.Form { Text = "Native manager dialog", ShowInTaskbar = false };
        Exception? failure = null;
        dialog.Shown += (_, _) =>
        {
            try
            {
                CheckModalCommands(window.ManagerMenu, profile);
                Entry(window.ManagerMenu, Texts.Get("de", "show_manager")).PerformClick();
                // A programmatic menu click may activate the dialog without taking foreground from another app.
                if (GetActiveWindow() != dialog.Handle) throw new Exception("Show manager must activate its native dialog.");
            }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); }
        };
        dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(window).Handle));
        if (failure != null) throw new Exception("Native modal tray check failed.", failure);
    }

    public static void ClickExit(MainWindow window)
    {
        var menu = window.ManagerMenu;
        Refresh(menu);
        Entry(menu, Texts.Get("de", "exit_manager")).PerformClick();
    }

    public static void Run(MainWindow window, Catalog catalog, string desktopFixture, Func<int> resourceSampleCount)
    {
        window.Width = 920;
        window.UpdateLayout();
        var menu = window.ManagerMenu;
        Refresh(menu);
        var first = catalog.Profiles()[0];
        var second = catalog.Profiles()[1];
        var originalItem = ProfileEntry(menu, second);
        if (first.Tray || second.Tray || !Entry(menu, "Manager anzeigen").Enabled ||
            originalItem.DropDownItems[0].Text != Texts.Get("de", "abrir") || originalItem.DropDownItems[1].Enabled)
            throw new Exception("A closed profile must be available in the localized tray menu, with Close disabled.");

        second = catalog.Save(second with { Name = "Arbeit & Privat" });
        var move = (Button)window.FindName("MoveDown");
        move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Refresh(menu);
        var target = ProfileEntry(menu, second);
        if (!originalItem.IsDisposed || menu.Items.IndexOf(target) >= menu.Items.IndexOf(ProfileEntry(menu, first)))
            throw new Exception("Reopening the tray menu must pick up a rename and the new catalog order.");

        string? previousLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", desktopFixture);
            string fixture = Path.Combine(desktopFixture, "AnthropicClaude", "claude.exe");
            if (!DesktopWindows.SamePath(CodeLauncher.FindDesktop(), fixture))
                throw new Exception("Tray checks require the temporary desktop recorder.");
            target.DropDownItems[0].PerformClick();
            CheckRunner.PumpUntil(() => Directory.Exists(second.DesktopPath) &&
                Directory.GetFiles(second.DesktopPath, "login-fixture-*.txt").Length == 1, "tray open of the second profile");
            if (catalog.Get("selected_code_key") != second.CodeKey || catalog.Profiles().Single(p => p.Id == second.Id).LastOpen == null)
                throw new Exception("Tray Open did not select and record the requested profile.");

            SystemCommands.CloseWindow(window);
            CheckRunner.PumpUntil(() => !window.IsVisible, "hide the manager before starting a desktop");
            WaitForTrayStatus(menu, first, running: false);
            string ready = Path.Combine(catalog.Home, "tray-window.ready");
            var start = new ProcessStartInfo(fixture) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--user-data-dir=" + first.DesktopPath);
            start.ArgumentList.Add(ready);
            using var running = Process.Start(start)!;
            try
            {
                CheckRunner.PumpUntil(() => File.Exists(ready), "fixture desktop window to open");
                WaitForTrayStatus(menu, first, running: true);
                int samplesBeforeClose = resourceSampleCount();
                ProfileEntry(menu, first).DropDownItems[1].PerformClick();
                CheckRunner.PumpUntil(() => running.HasExited, "tray Close to exit the fixture desktop");
                WaitForTrayStatus(menu, first, running: false);
                if (resourceSampleCount() != samplesBeforeClose)
                    throw new Exception("The hidden manager sampled process resources while updating desktop status.");
                if (window.IsVisible || catalog.Get("selected_code_key") != second.CodeKey)
                    throw new Exception("Tray Close must keep the manager hidden and preserve its selected profile.");
                Entry(menu, Texts.Get("de", "show_manager")).PerformClick();
                CheckRunner.PumpUntil(() => window.IsVisible, "restore from the Show manager tray entry");
                if (((TextBlock)window.FindName("SelectedName")).Text != second.Name)
                    throw new Exception("Restoring the manager after a tray action changed the displayed profile.");
            }
            finally { if (!running.HasExited) { running.Kill(); running.WaitForExit(); } }

            var dialog = new Window { Owner = window, ShowInTaskbar = false, Width = 100, Height = 100 };
            try
            {
                dialog.Show();
                CheckModalCommands(menu, first);
            }
            finally { dialog.Close(); }
            CheckNativeModal(window, first);

            Refresh(menu);
            var removed = catalog.Profiles().Single(profile => profile.Provider == ProfileProvider.Codex);
            var stale = ProfileEntry(menu, removed);
            catalog.Delete(removed, false);
            var replacement = catalog.Save(catalog.Draft() with { Name = "Replacement", Shortcut = "" });
            if (replacement.Id != removed.Id) throw new Exception("The fixture requires recycling the deleted profile's numeric ID.");
            stale.DropDownItems[0].PerformClick();
            if (Directory.Exists(replacement.DesktopPath) || catalog.Profiles().Single(p => p.Id == replacement.Id).LastOpen != null)
                throw new Exception("A stale tray action must never open a different profile reusing the same numeric ID.");
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocal); }
        Console.WriteLine("PASS localized tray menu, live profile order, targeted Open and Close, modal guards and stale actions");
    }
}
