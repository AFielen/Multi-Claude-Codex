using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MultiClaude;

internal static class PublishedSmoke
{
    private sealed record LegacyProfile(int Id, string Name, string StoredPath, string Key);

    public static void Run(string executable, string root)
    {
        string home = Directory.CreateDirectory(Path.Combine(root, "Testkatalog")).FullName;
        string receipts = Directory.CreateDirectory(Path.Combine(root, "receipts")).FullName;
        string user = Path.Combine(root, "fictional user");
        string roaming = Directory.CreateDirectory(Path.Combine(user, "AppData", "Roaming")).FullName;
        string local = Path.Combine(user, "AppData", "Local");
        string desktopDirectory = Directory.CreateDirectory(Path.Combine(local, "AnthropicClaude")).FullName;
        BuildRecorder(root, Path.Combine(desktopDirectory, "claude.exe"));

        ProcessStartInfo StartInfo(string app, string? catalog, string role, params string[] args)
        {
            var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = root };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            start.Environment["USERPROFILE"] = user;
            start.Environment["APPDATA"] = roaming;
            start.Environment["LOCALAPPDATA"] = local;
            start.Environment["MULTI_CLAUDE_SMOKE_RECEIPTS"] = receipts;
            start.Environment["MULTI_CLAUDE_SMOKE_ROLE"] = role;
            if (catalog == null) start.Environment.Remove("MULTI_CLAUDE_HOME");
            else start.Environment["MULTI_CLAUDE_HOME"] = catalog;
            return start;
        }

        string portable = Directory.CreateDirectory(Path.Combine(root, "blank portable")).FullName;
        string package = Path.GetDirectoryName(executable)!;
        string portableApp = Path.Combine(portable, Path.GetFileName(executable));
        File.Copy(executable, portableApp);
        foreach (string notice in new[] { "LICENSE", "NOTICE", "AUTHORS" })
            File.Copy(Path.Combine(package, notice), Path.Combine(portable, notice));
        string gallery = Directory.CreateDirectory(Path.Combine(portable, "gallery")).FullName;
        foreach (string image in "astronaut carp comet cosmic-cat dragon fox ghosty owl pot robot".Split(' '))
            File.Copy(Path.Combine(package, "gallery", image + ".png"), Path.Combine(gallery, image + ".png"));

        var blankStart = StartInfo(portableApp, null, "manager");
        WithProcess(blankStart, blank =>
        {
            nint welcome = WaitForWindow(blank, owned: true);
            Check(File.Exists(Path.Combine(portable, "perfis.db")), "Missing catalog beside portable executable.");
            CloseWindow(welcome);
            WaitFor(blank, () =>
            {
                using var db = new Sqlite(Path.Combine(portable, "perfis.db"), readOnly: true);
                return !string.IsNullOrWhiteSpace(db.Scalar("SELECT valor FROM config WHERE chave='idioma'"));
            }, "the first-run language to be saved");
            nint main = WaitForWindow(blank);
            HideToTray(blank, main);
            WithProcess(blankStart, WaitForSuccess);
            WaitFor(blank, () => FindWindow(blank.Id, owned: false) == main, "the second launch to restore the original window");
            EndSession(blank);
        });
        Console.WriteLine("PASS portable catalog, first-run language, close to tray and restore on a second launch");

        LegacyProfile[] profiles =
        [
            new(41, "Arbeit", @"dados\Arbeit", new string('a', 32)),
            new(87, "Privat", Path.Combine(root, "external fixture"), new string('b', 32))
        ];
        CreateLegacyCatalog(home, profiles);
        var seen = new HashSet<string>();
        string unusedHome = Path.Combine(root, "unused");
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            // A shortcut starts the stopped manager and opens its profile on the second run.
            string? coldKey = cycle == 2 ? profiles[^1].Key : null;
            string[] args = coldKey == null ? [] : ["--open-profile", coldKey, "--manager-home", home];
            WithProcess(StartInfo(executable, home, "manager", args), manager =>
            {
                HideToTray(manager, WaitForWindow(manager));
                foreach (var profile in profiles.Reverse())
                {
                    if (profile.Key != coldKey)
                        WithProcess(StartInfo(executable, unusedHome, "secondary", "--open-profile", profile.Key, "--manager-home", home), WaitForSuccess);
                    string[] NewReceipts() => Directory.GetFiles(receipts, "*.receipt").Except(seen).ToArray();
                    WaitFor(manager, () => NewReceipts().Length != 0, $"profile {profile.Id} to launch on run {cycle}");
                    string receipt = NewReceipts().Single();
                    seen.Add(receipt);
                    string code = Path.Combine(home, "claude-code", profile.Key);
                    string[] expected = ["manager", "--user-data-dir=" + Path.GetFullPath(profile.StoredPath, home), code, Path.Combine(code, "anthropic")];
                    Check(File.ReadAllLines(receipt).SequenceEqual(expected), $"Incorrect launcher or account paths for profile {profile.Id}.");
                }
                EndSession(manager);
            });
            using var db = new Sqlite(Path.Combine(home, "perfis.db"), readOnly: true);
            var rows = db.Query("SELECT id,pasta,code_key FROM perfis JOIN profile_options ON id=profile ORDER BY id");
            Check(rows.Count == profiles.Length && profiles.Zip(rows).All(pair =>
                pair.Second.SequenceEqual([pair.First.Id.ToString(), pair.First.StoredPath, pair.First.Key])), "Catalog identities changed.");
            Check(db.Scalar("SELECT count(*) FROM profile_options WHERE last_open IS NOT NULL") == "2", "Missing activation time.");
            Check(db.Scalar("SELECT count(*) FROM config WHERE chave IN ('login_pasta','login_ok','login_url')") == "0", "Obsolete login state persisted.");
            Check(!Directory.Exists(unusedHome) && Directory.GetFiles(receipts, "*.receipt").Length == seen.Count, "Unexpected secondary catalog or duplicate launch.");
            Console.WriteLine($"PASS published WPF run {cycle}: shortcuts reached the hidden manager with the original profile paths");
        }
    }

    private static void CreateLegacyCatalog(string home, LegacyProfile[] profiles)
    {
        using var db = new Sqlite(Path.Combine(home, "perfis.db"));
        LegacyCatalogFixture.CreateSchema(db);
        foreach (var p in profiles)
        {
            string folder = Directory.CreateDirectory(Path.GetFullPath(p.StoredPath, home)).FullName;
            File.WriteAllText(Path.Combine(folder, "Local State"), "{}");
            File.WriteAllText(Path.Combine(folder, "config.json"), "{}");
            db.Run("INSERT INTO perfis(id,nome,pasta) VALUES(?,?,?)", p.Id, p.Name, p.StoredPath);
            db.Run("INSERT INTO profile_options(profile,code_key,position,color,shortcut,isolated) VALUES (?,?,?,'#336699','',1)", p.Id, p.Key, p.Id);
        }
        db.Run("INSERT INTO config VALUES ('idioma','de'),('tema','claro'),('login_url','obsolete'),('login_pasta','obsolete'),('login_ok','1')");
    }

    private static void BuildRecorder(string root, string target)
    {
        string source = Path.Combine(root, "FixtureDesktop.cs");
        File.WriteAllText(source, """
            using System;
            using System.IO;
            class FixtureDesktop {
                static int Main(string[] args) {
                    string output = Environment.GetEnvironmentVariable("MULTI_CLAUDE_SMOKE_RECEIPTS");
                    if (!Directory.Exists(output) || args.Length != 1) return 2;
                    string path = Path.Combine(output, Guid.NewGuid().ToString("N") + ".pending");
                    File.WriteAllLines(path, new[] {
                        Environment.GetEnvironmentVariable("MULTI_CLAUDE_SMOKE_ROLE"), args[0],
                        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetEnvironmentVariable("ANTHROPIC_CONFIG_DIR")
                    });
                    File.Move(path, Path.ChangeExtension(path, ".receipt"));
                    return 0;
                }
            }
            """);
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        var build = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "/nologo", "/target:winexe", "/out:" + target, source }) build.ArgumentList.Add(arg);
        WithProcess(build, WaitForSuccess);
    }

    private static void WithProcess(ProcessStartInfo start, Action<Process> operation)
    {
        using var process = Process.Start(start)!;
        try { operation(process); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(20000);
            }
        }
    }

    private static void WaitForSuccess(Process process)
    {
        Check(process.WaitForExit(20000), $"Process {process.Id} timed out.");
        Check(process.ExitCode == 0, $"Process {process.Id} exited with code {process.ExitCode}.");
    }

    private static void WaitFor(Process manager, Func<bool> ready, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(40))
        {
            Check(!manager.HasExited, $"Manager exited while waiting for {description}.");
            if (ready()) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static nint WaitForWindow(Process manager, bool owned = false)
    {
        nint hwnd = 0;
        WaitFor(manager, () => (hwnd = FindWindow(manager.Id, owned)) != 0, owned ? "the first-run dialog" : "the manager window");
        return hwnd;
    }

    private static nint FindWindow(int pid, bool owned)
    {
        foreach (nint hwnd in WindowsOf(pid))
        {
            if (!IsWindowVisible(hwnd) || (GetWindow(hwnd, 4 /* GW_OWNER */) != 0) != owned) continue;
            var title = new StringBuilder(256);
            GetWindowTextW(hwnd, title, title.Capacity);
            if (owned || title.ToString().StartsWith("Multi Claude", StringComparison.Ordinal)) return hwnd;
        }
        return 0;
    }

    private static List<nint> WindowsOf(int pid)
    {
        var result = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid) result.Add(hwnd);
            return true;
        }, 0);
        return result;
    }

    private static void HideToTray(Process manager, nint hwnd)
    {
        CloseWindow(hwnd);
        WaitFor(manager, () => IsWindow(hwnd) && !IsWindowVisible(hwnd), "the manager to hide in the notification area");
    }

    private static void CloseWindow(nint hwnd) => Check(PostMessageW(hwnd, 0x0010, 0, 0), "Could not post WM_CLOSE.");

    private static void EndSession(Process manager)
    {
        // Simulate logoff only for windows belonging to this fixture process.
        foreach (nint hwnd in WindowsOf(manager.Id))
        {
            PostMessageW(hwnd, 0x0011 /* WM_QUERYENDSESSION */, 0, 0);
            PostMessageW(hwnd, 0x0016 /* WM_ENDSESSION */, 1, 0);
        }
        WaitForSuccess(manager);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private delegate bool EnumWindow(nint hwnd, nint unused);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
