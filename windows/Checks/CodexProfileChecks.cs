using System.Diagnostics;
using MultiClaude;

internal static class CodexProfileChecks
{
    internal static void Run(string home)
    {
        var catalog = new Catalog(home);
        Profile Create(string name) => catalog.Save(catalog.Draft() with { Provider = ProfileProvider.Codex, Name = name });
        var personal = Create("Personal");
        var work = Create("Work");
        var removed = Create("Removed");
        catalog.Delete(removed, erase: false);
        var fixture = Path.Combine(home, "ChatGPT.exe");
        var cli = Path.Combine(home, "codex.exe");
        var source = Path.Combine(home, "Recorder.cs");
        File.WriteAllText(source, """
            using System;
            using System.IO;
            using System.Windows.Forms;
            class Recorder {
                [STAThread] static void Main(string[] args) {
                    string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                    if (String.IsNullOrEmpty(home)) return;
                    string root = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "codex-home");
                    if (!String.Equals(Path.GetDirectoryName(home), root, StringComparison.OrdinalIgnoreCase)) return;
                    bool desktop = args.Length > 0 && args[0].StartsWith("--user-data-dir=");
                    string file = Path.Combine(home, desktop ? "desktop.txt" : "terminal.txt");
                    string[] lines = {Environment.GetEnvironmentVariable("CODEX_ELECTRON_USER_DATA_PATH"),
                        Environment.GetEnvironmentVariable("OPENAI_API_KEY") == null ? "" : "set", String.Join(" ", args)};
                    File.WriteAllLines(file + ".tmp", lines);
                    File.Move(file + ".tmp", file);
                    if (desktop) Application.Run(new Form {
                        Opacity = 0, ShowInTaskbar = true, StartPosition = FormStartPosition.Manual,
                        Location = new System.Drawing.Point(-30000, -30000)
                    });
                }
            }
            """);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        var build = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true };
        string icon = Path.Combine(home, "fixture.ico");
        using (var file = File.Create(icon)) System.Drawing.SystemIcons.Information.Save(file);
        foreach (string arg in new[]{"/nologo", "/target:winexe", "/out:" + fixture, "/win32icon:" + icon,
                     "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", source})
            build.ArgumentList.Add(arg);
        using (var process = Process.Start(build)!)
            if (!process.WaitForExit(20000) || process.ExitCode != 0) throw new Exception("Could not compile Codex fixture.");
        File.Copy(fixture, cli);
        string Resolve(ProfileProvider provider, string path) =>
            provider == ProfileProvider.Codex ? path : throw new Exception("Codex profile dispatched to Claude");
        using var manager = new WindowsIntegration(home, provider => Resolve(provider, fixture), provider => Resolve(provider, cli));
        using var desktops = new DesktopWindows(ProfileProvider.Codex);
        using var claude = new DesktopWindows();
        var profiles = new[] { personal, work };
        manager.SyncProfiles(profiles);
        try
        {
            try
            {
                manager.OpenCode(removed);
                throw new Exception("A stale Codex profile started a terminal.");
            }
            catch (InvalidOperationException) { }
            foreach (var p in profiles)
            {
                manager.OpenDesktop(p);
                manager.OpenCode(p, login: p == personal);
                if (!manager.CodeCommand(p).Contains(p.CodeKey)) throw new Exception("Copied command lost profile identity.");
                var code = Path.Combine(home, "codex-home", p.CodeKey);
                var desktopReceipt = Path.Combine(code, "desktop.txt");
                var cliReceipt = Path.Combine(code, "terminal.txt");
                if (!SpinWait.SpinUntil(() => File.Exists(desktopReceipt) && File.Exists(cliReceipt), TimeSpan.FromSeconds(12)))
                    throw new Exception("Codex Desktop and terminal did not start in the same profile.");
                var desktop = File.ReadAllLines(desktopReceipt);
                var terminal = File.ReadAllLines(cliReceipt);
                if (!desktop.SequenceEqual([p.DesktopPath, "", "--user-data-dir=" + p.DesktopPath])
                    || terminal[1] != "" || terminal[2] != (p == personal ? "login" : ""))
                    throw new Exception("Incorrect Codex launch arguments or credential environment.");
            }
            var a = desktops.Matching(personal.DesktopPath, fresh: true);
            var b = desktops.Matching(work.DesktopPath);
            if (a.Count != 1 || b.Count != 1 || a[0] == b[0])
                throw new Exception("Concurrent Codex windows were not identified separately.");
            using var first = Process.GetProcessById(a[0]);
            using var second = Process.GetProcessById(b[0]);
            bool HasGroup(Process process)
            {
                process.Refresh();
                return process.MainWindowHandle != 0 &&
                    TaskbarGroup.Read(process.MainWindowHandle, TaskbarGroup.AppId)?.StartsWith("RichardKPL.MultiClaude.Profile.") == true;
            }
            if (!SpinWait.SpinUntil(() => profiles.All(manager.IsRunning) && HasGroup(first) && HasGroup(second), TimeSpan.FromSeconds(10)))
                throw new Exception("The manager did not assign taskbar groups to both running Codex windows.");
            var groupA = TaskbarGroup.Read(first.MainWindowHandle, TaskbarGroup.AppId);
            var groupB = TaskbarGroup.Read(second.MainWindowHandle, TaskbarGroup.AppId);
            if (groupA == groupB || (TaskbarGroup.Read(second.MainWindowHandle, TaskbarGroup.RelaunchCommand)?.Contains(work.CodeKey) == true &&
                    TaskbarGroup.Read(second.MainWindowHandle, TaskbarGroup.RelaunchName) != "Codex · Work"))
                throw new Exception("Codex windows must have separate taskbar groups and a provider-specific relaunch name.");
            if (claude.Matching(personal.DesktopPath, fresh: true).Count != 0)
                throw new Exception("Claude matched a Codex window.");
            manager.SyncProfiles([work]);
            if (HasGroup(first) || !HasGroup(second))
                throw new Exception("Removing a Codex profile should restore only its taskbar group.");
            manager.Dispose();
            if (HasGroup(second)) throw new Exception("Disposing the manager did not restore the Codex taskbar group.");
            manager.CloseDesktop(personal);
            if (!manager.IsRunning(work)) throw new Exception("Closing one profile affected the other.");
            if (Usage.SetCapture(work, true) || Directory.Exists(Path.Combine(home, "claude-code")))
                throw new Exception("Codex entered Claude-specific usage or config paths.");
        }
        finally
        {
            foreach (var profile in profiles)
                foreach (int pid in desktops.Matching(profile.DesktopPath, fresh: true))
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.MainModule?.FileName == fixture && !process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit();
                    }
                }
        }
    }
}
