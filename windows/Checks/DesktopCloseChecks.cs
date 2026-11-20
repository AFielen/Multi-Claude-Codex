using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MultiClaude;

internal static class DesktopCloseChecks
{
    public static void Run(MainWindow window, Profile profile, string fixtureRoot)
    {
        string ready = Path.Combine(fixtureRoot, "close-button.ready");
        string gate = ready + ".continue";
        var start = new ProcessStartInfo(Path.Combine(fixtureRoot, "AnthropicClaude", "claude.exe")) { UseShellExecute = false };
        start.ArgumentList.Add("--user-data-dir=" + profile.DesktopPath);
        start.ArgumentList.Add(ready);
        start.Environment["MULTI_CLAUDE_FIXTURE_TRAY"] = "1";
        start.Environment["MULTI_CLAUDE_FIXTURE_SAVE_GATE"] = gate;
        using var desktop = Process.Start(start)!;
        try
        {
            var button = (Button)window.FindName("CloseDesktopButton");
            CheckRunner.PumpUntil(() => File.Exists(ready) && button.IsVisible && button.IsEnabled,
                "the visible manager to offer Close for the selected desktop");
            using var warning = new ManagerWarningMonitor(window);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CheckRunner.PumpUntil(() => warning.Detected || File.Exists(gate + ".saving"),
                "the selected desktop to start saving");
            warning.ThrowIfShown();
            (bool Exited, bool CloseEnabled)? duringSave = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                duringSave = (desktop.HasExited, button.IsEnabled);
                File.WriteAllText(gate, "continue");
            }));
            CheckRunner.PumpUntil(() => duringSave.HasValue, "input while the selected desktop is saving");
            if (duringSave is { Exited: true }) throw new Exception("Close blocked dispatcher input until the selected desktop exited.");
            if (duringSave is { CloseEnabled: true }) throw new Exception("Close allowed a duplicate click while the desktop was saving.");
            CheckRunner.PumpUntil(() => warning.Detected || desktop.HasExited && !button.IsVisible && button.IsEnabled,
                "a single Close click to exit the tray-resident desktop and show its closed status");
            warning.ThrowIfShown();
            if (!File.Exists(ready + ".closed"))
                throw new Exception("Graceful Close must finish without a warning and clear its pending state.");
            Console.WriteLine("PASS visible manager closes a tray-resident desktop with one click while processing input");
        }
        finally { if (!desktop.HasExited) { desktop.Kill(); desktop.WaitForExit(); } }
    }

}
