using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MultiClaude;

internal static class IntegrationChecks
{
    public static void Run(string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        var app = new WindowsIntegration(tempRoot);
        var one = new Profile(1, "Amber", @"dados\one", Path.Combine(tempRoot, "dados", "one"), null,
            "aabbccddeeff00112233445566778899", 0, "#d97757", "Ctrl+Alt+1", true, false, false, null, false)
        { CodeHome = tempRoot };
        var two = one with { Id = 2, Name = "Blue", DesktopPath = Path.Combine(tempRoot, "dados", "two"), CodeKey = "aabbccddeeff00112233445566779988" };
        Check(app.CodeDirectory(one) != app.CodeDirectory(two), "Code roots must follow keys.");
        var child = new ProcessStartInfo();
        child.Environment["ANTHROPIC_AUTH_TOKEN"] = "fixture";
        child.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        CodeLauncher.Isolate(child, app.CodeDirectory(one));
        Check(child.Environment["CLAUDE_CONFIG_DIR"] == app.CodeDirectory(one)
            && !child.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN")
            && child.Environment.ContainsKey("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"), "Isolated child environment");
        Check(File.Exists(app.CodeDirectory(two)) == false, "Independent config folder");
        Check(WindowsIntegration.ValidLink("claude://claude.ai/magic-link?code=fixture")
            && WindowsIntegration.ValidLink("claude://login/fixture")
            && !WindowsIntegration.ValidLink("https://claude.ai/") && !WindowsIntegration.ValidLink("claude://login/??"), "Callback routes");
        Check(ProfileBindings.NormalizeShortcut("strg + alt + 2") == "Ctrl+Alt+2", "Localized shortcut");
        try { ProfileBindings.NormalizeShortcut("F12"); throw new Exception("Accepted F12"); } catch (ArgumentException) { }
        CheckNativeShortcut();
        CheckDesktopWindows(tempRoot);
        Directory.CreateDirectory(one.DesktopPath);
        Directory.CreateDirectory(Path.Combine(one.DesktopPath, "logs"));
        File.WriteAllText(Path.Combine(one.DesktopPath, "Local State"), "{}");
        File.WriteAllText(Path.Combine(one.DesktopPath, "config.json"), """{"lastKnownAccountUuid":"fixture","theme":"dark","oauth:example":"fake"}""");
        Check(app.IsSignedIn(one), "Signed-in fixture");
        var log = Path.Combine(one.DesktopPath, "logs", "main.log");
        File.WriteAllText(log, "Login-state transition (loggedOut: false → true)");
        Check(!app.IsSignedIn(one), "Most recent signed-out log takes precedence");
        WindowsIntegration.ClearSessionFiles(one.DesktopPath);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(one.DesktopPath, "config.json")));
        Check(json.RootElement.GetProperty("theme").GetString() == "dark" && json.RootElement.EnumerateObject().Count() == 1, "Sign-out preserves preferences");
        Check(app.CodeCommand(one).Contains(one.CodeKey) && app.CodeCommand(one with { Isolated = false }).Contains("shared"), "Copied command identity");
        var fakeUser = Path.Combine(tempRoot, "sample user");
        Directory.CreateDirectory(Path.Combine(fakeUser, "skills"));
        File.WriteAllText(Path.Combine(fakeUser, "CLAUDE.md"), "Fixture only");
        CodeSharing.Sync(app.CodeDirectory(one), fakeUser, true, false);
        var skills = Path.Combine(app.CodeDirectory(one), "skills");
        Check(new DirectoryInfo(skills).LinkTarget != null, "Missing shared junction");
        File.WriteAllText(Path.Combine(app.CodeDirectory(one), "CLAUDE.md"), "Replaced by user");
        CodeSharing.Sync(app.CodeDirectory(one), fakeUser, false, false);
        Check(!Directory.Exists(skills) && Directory.Exists(Path.Combine(fakeUser, "skills"))
            && File.ReadAllText(Path.Combine(app.CodeDirectory(one), "CLAUDE.md")) == "Replaced by user", "Unshare preserves user resources");
        var link = Path.Combine(tempRoot, "fixture.lnk");
        app.SaveShortcut(one, link);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", true)!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(link);
        try
        {
            string arguments = shortcut.Arguments;
            Check(arguments.Contains("--open-profile " + one.CodeKey) && arguments.Contains(tempRoot), "Exported shortcut target");
            string icon = shortcut.IconLocation;
            Check(File.Exists(icon[..icon.LastIndexOf(',')]), "Exported profile icon");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void CheckNativeShortcut()
    {
        var window = new Window { ShowInTaskbar = false };
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();
        long activated = 0;
        using var hotkeys = new ProfileBindings(window, id => activated = id);
        try
        {
            int key = 23, token = 1;
            for (; key >= 19; key--, token++)
            {
                try { hotkeys.SetHotkey(42, "Ctrl+Alt+Shift+F" + key); break; }
                catch (InvalidOperationException) when (key > 19) { } // Pre-existing user registration.
            }
            uint vk = (uint)(0x70 + key - 1);
            const int otherToken = 0x4300;
            bool conflict = RegisterHotKey(hwnd, otherToken, 0x4007, vk);
            if (conflict) UnregisterHotKey(hwnd, otherToken);
            Check(!conflict, "Windows must own the registered shortcut.");
            Check(PostMessageW(hwnd, 0x312, token, (nint)((vk << 16) | 7)), "Native WM_HOTKEY dispatch");
            Pump();
            Check(activated == 42, "Native shortcut activates its assigned profile");
            hotkeys.Dispose();
            Check(RegisterHotKey(hwnd, otherToken, 0x4007, vk), "Shortcut should be available after cleanup");
            UnregisterHotKey(hwnd, otherToken);
        }
        finally { window.Close(); }
    }

    private static void CheckDesktopWindows(string root)
    {
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        string source = Path.Combine(root, "FixtureWindow.cs");
        string fixtureLocal = Path.Combine(root, "fictional-appdata");
        string program = Path.Combine(Directory.CreateDirectory(Path.Combine(fixtureLocal, "AnthropicClaude")).FullName, "claude.exe");
        File.WriteAllText(source, """
            using System;
            using System.Drawing;
            using System.Windows.Forms;
            internal class FixtureWindow : Form {
                protected override bool ShowWithoutActivation { get { return true; } }
                [STAThread] static void Main(string[] args) {
                    if (args.Length != 2 || !args[1].EndsWith(".ready", StringComparison.Ordinal)) {
                        var folder = args[0].Substring("--user-data-dir=".Length);
                        var receipt = System.IO.Path.Combine(folder, "login-fixture-" + Guid.NewGuid());
                        System.IO.File.WriteAllLines(receipt + ".tmp", args);
                        System.IO.File.Move(receipt + ".tmp", receipt + ".txt");
                        return;
                    }
                    var window = new FixtureWindow();
                    window.Opacity = 0;
                    window.StartPosition = FormStartPosition.Manual;
                    window.Location = new Point(-30000, -30000);
                    window.ShowInTaskbar = true;
                    window.Text = "Multi Claude test · " + System.IO.Path.GetFileName(args[1]);
                    window.Shown += delegate { System.IO.File.WriteAllText(args[1], "ready"); };
                    Application.Run(window);
                }
            }
            """);
        var build = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true };
        string fixtureIcon = Path.Combine(root, "fixture.ico");
        using (var file = File.Create(fixtureIcon)) System.Drawing.SystemIcons.Information.Save(file);
        foreach (var argument in new[] { "/nologo", "/target:winexe", "/out:" + program, "/win32icon:" + fixtureIcon, "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", source })
            build.ArgumentList.Add(argument);
        using (var compile = Process.Start(build)!)
        {
            Check(compile.WaitForExit(20000) && compile.ExitCode == 0, "Build two synthetic Desktop windows");
        }
        string first = Path.Combine(root, "First desktop"), second = Path.Combine(root, "Second desktop");
        Process Start(string path)
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--user-data-dir=" + path);
            start.ArgumentList.Add(path + ".ready");
            return Process.Start(start)!;
        }
        using var a = Start(first);
        using var b = Start(second);
        try
        {
            Check(SpinWait.SpinUntil(() => File.Exists(first + ".ready") && File.Exists(second + ".ready"),
                TimeSpan.FromSeconds(15)), "Synthetic Desktop ready");
            var processes = new DesktopWindows();
            var pidA = processes.Matching(first, fresh: true);
            var pidB = processes.Matching(second);
            Check(pidA.SequenceEqual([a.Id]) && pidB.SequenceEqual([b.Id]), "WMI must match each Windows process to its exact profile");
            var catalog = new Catalog(Path.Combine(root, "resource catalog"));
            var profiles = new[] { catalog.Draft() with { Name = "Test Amber", DesktopPath = first, CodeKey = new string('a', 32), Color = "#ddaa11", Border = false },
                catalog.Draft() with { Name = "Test Blue", DesktopPath = second, CodeKey = new string('b', 32), Color = "#3366bb" } };
            using var monitor = new WindowsIntegration(catalog.Home);
            monitor.SyncProfiles(profiles);
            Check(profiles.All(p => monitor.IsRunning(p with { LastOpen = "123" })), "Both newly opened profiles should be running");
            var hwndA = a.MainWindowHandle;
            var hwndB = b.MainWindowHandle;
            Check(WindowDot(hwndA, 1) == "#ddaa11" && WindowDot(hwndB, 0) == "#3366bb",
                "Uncombined taskbar buttons should display the profile dot on the live large and small window icons");
            var groupA = TaskbarGroup.Read(hwndA, TaskbarGroup.AppId);
            var groupB = TaskbarGroup.Read(hwndB, TaskbarGroup.AppId);
            Check(!string.IsNullOrEmpty(groupA) && !string.IsNullOrEmpty(groupB) && groupA != groupB,
                "Windows must give the two running profiles separate taskbar buttons");
            string? IconFile(nint hwnd) => TaskbarGroup.Read(hwnd, TaskbarGroup.RelaunchIcon) is { } resource && resource.EndsWith(",0")
                ? resource[..^2] : null;
            string? amber = IconFile(hwndA), blue = IconFile(hwndB);
            Check(File.Exists(amber) && File.Exists(blue) && amber != blue,
                "Each taskbar button should use its profile icon, including with the window border disabled");
            var edited = profiles[0] with { Color = "#50bb77" };
            monitor.SyncProfiles([edited, profiles[1]]);
            Check(monitor.IsRunning(edited) && IconFile(hwndA) is string green
                && File.Exists(green) && green != amber && IconFile(hwndB) == blue && TaskbarGroup.Read(hwndA, TaskbarGroup.AppId) == groupA
                && WindowDot(hwndA, 0) == "#50bb77" && WindowDot(hwndB, 1) == "#3366bb",
                "Editing an open profile should update just its taskbar color and retain its group");
            monitor.SyncProfiles([profiles[1]]);
            Check(monitor.IsRunning(edited) && TaskbarGroup.Read(hwndA, TaskbarGroup.AppId) != groupA
                && WindowDot(hwndA, 0) != "#50bb77" && WindowDot(hwndB, 1) == "#3366bb",
                "Removing a running profile should restore its window and ignore a stale poll");
            var initial = monitor.ReadProcessResources(profiles);
            Check(initial.Count == 2 && profiles.All(p => initial[p.CodeKey] is { CpuPercent: null, RamBytes: > 0 }),
                "Both live Desktop profiles should report their own RAM before a CPU interval is available");
            DesktopWindows.Close(pidA);
            Check(a.WaitForExit(5000) && !b.HasExited, "Graceful close affected only the selected Desktop");
            var remaining = monitor.ReadProcessResources(profiles);
            Check(remaining.Count == 1 && remaining.TryGetValue(profiles[1].CodeKey, out var stillOpen)
                && stillOpen is { CpuPercent: >= 0 and <= 100, RamBytes: > 0 }, "A closed profile cannot retain a reading");
            DesktopWindows.Close(pidB);
            Check(b.WaitForExit(5000), "Second Desktop closed gracefully");
        }
        finally
        {
            foreach (var fixture in new[] { a, b })
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(); }
        }
        string? previousLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", fixtureLocal);
            Check(DesktopWindows.SamePath(CodeLauncher.FindDesktop(), program), "Run login test only against the temporary Desktop recorder");
            CheckExplicitLogin(Path.Combine(root, "manual login"));
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocal); }
    }

    private static string? WindowDot(nint hwnd, int kind)
    {
        nint handle = SendMessageW(hwnd, 0x7f /* WM_GETICON */, kind, 0);
        if (handle == 0) return null;
        using var icon = System.Drawing.Icon.FromHandle(handle);
        using var bitmap = icon.ToBitmap();
        var pixel = bitmap.GetPixel(bitmap.Width * 4 / 5, bitmap.Height * 4 / 5);
        return $"#{pixel.R:x2}{pixel.G:x2}{pixel.B:x2}";
    }

    private static void CheckExplicitLogin(string home)
    {
        var catalog = new Catalog(home);
        var alpha = catalog.Save(catalog.Draft() with { Name = "Fixture Alpha" });
        var beta = catalog.Save(catalog.Draft() with { Name = "Fixture Beta" });
        var manager = new WindowsIntegration(home);
        const string link = "claude://login/synthetic-test-callback";
        void MustReject(Profile target)
        {
            try { manager.SendLoginLink(target, link); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Rejected login attempt launched");
        }
        manager.BeginLogin(alpha);
        MustReject(beta);
        manager.CancelLogin();
        MustReject(alpha);
        manager.BeginLogin(alpha);
        manager.SendLoginLink(alpha, link);
        MustReject(alpha);
        Check(SpinWait.SpinUntil(() => Directory.GetFiles(alpha.DesktopPath, "login-fixture-*.txt").Length >= 3,
            TimeSpan.FromSeconds(8)), "Read both sign-in starts and one confirmed callback from the recorder");
        var receipts = Directory.GetFiles(alpha.DesktopPath, "login-fixture-*.txt").Select(File.ReadAllLines).ToArray();
        string desktop = "--user-data-dir=" + alpha.DesktopPath;
        Check(receipts.Length == 3 && receipts.Count(args => args.SequenceEqual([desktop])) == 2
            && receipts.Count(args => args.SequenceEqual([desktop, link])) == 1, "Only one explicitly confirmed callback was delivered to Alpha");
        Check(!Directory.Exists(beta.DesktopPath) || Directory.GetFiles(beta.DesktopPath, "login-fixture-*.txt").Length == 0,
            "Mismatched profile received no login callback");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int token, uint mods, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int token);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
