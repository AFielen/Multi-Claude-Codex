using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MultiClaude;

public sealed class WindowsIntegration
{
    private readonly string home;
    private readonly Dictionary<string, long> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly DesktopWindows claudeWindows = new();
    private readonly DesktopWindows codexWindows = new(ProfileProvider.Codex);
    private readonly Func<ProfileProvider, string> desktopProgram;
    private readonly Func<ProfileProvider, string> terminalProgram;
    private readonly ProcessResources resources = new();
    private (string Key, string Folder, long Expires)? login;
    private readonly HashSet<int> colored = [];

    public WindowsIntegration(string home) : this(home,
        provider => provider == ProfileProvider.Codex ? CodexLauncher.FindDesktop() : CodeLauncher.FindDesktop(),
        provider => provider == ProfileProvider.Codex ? CodexLauncher.FindCode() : CodeLauncher.FindCode()) { }

    internal WindowsIntegration(string home, Func<ProfileProvider, string> desktopProgram, Func<ProfileProvider, string> terminalProgram)
    {
        StoragePaths.RequireOrdinary(home);
        this.home = Path.GetFullPath(home);
        this.desktopProgram = desktopProgram;
        this.terminalProgram = terminalProgram;
    }

    private DesktopWindows Windows(ProfileProvider provider) => provider == ProfileProvider.Codex ? codexWindows : claudeWindows;

    public void OpenDesktop(Profile p)
    {
        p = CurrentProfile(p);
        var pids = Windows(p.Provider).Matching(p.DesktopPath, fresh: true);
        if (DesktopWindows.Focus(pids)) return;
        if (pending.TryGetValue(p.DesktopPath, out var expires) && Environment.TickCount64 < expires) return;
        LaunchDesktop(p, null);
        pending[p.DesktopPath] = Environment.TickCount64 + 4000;
    }

    private Profile CurrentProfile(Profile p)
    {
        var current = Catalog.FindInExisting(home, p.CodeKey);
        if (current is null || current.Id != p.Id || current.Provider != p.Provider ||
            !DesktopWindows.SamePath(current.DesktopPath, p.DesktopPath) || !DesktopWindows.SamePath(current.CodeHome, p.CodeHome))
            throw new InvalidOperationException("Dieses Profil ist nicht mehr im Katalog verfügbar.");
        new Catalog(home).ValidateDesktop(current);
        return current;
    }

    private void LaunchDesktop(Profile current, string? link)
    {
        var program = desktopProgram(current.Provider);
        var start = CodeLauncher.StartInfo(program);
        start.ArgumentList.Add("--user-data-dir=" + current.DesktopPath);
        if (link != null)
        {
            if (current.Provider != ProfileProvider.Claude) throw new ArgumentException("Anmeldelinks gehören zu Claude Desktop.");
            start.ArgumentList.Add(link);
        }
        PrepareEnvironment(start, current);
        Directory.CreateDirectory(current.DesktopPath);
        using var launched = Process.Start(start);
    }

    public void BeginLogin(Profile p)
    {
        login = null;
        if (p.Provider == ProfileProvider.Codex) { OpenDesktop(p); return; }
        LaunchDesktop(CurrentProfile(p), null);
        login = (p.CodeKey, p.DesktopPath, Environment.TickCount64 + 300_000);
    }

    public void CancelLogin() => login = null;

    public IReadOnlySet<string> RunningFolders() => claudeWindows.Folders();

    internal Dictionary<string, ResourceReading> ReadProcessResources(IReadOnlyList<Profile> profiles)
    {
        var roots = CodeProcessRegistry.Active(home, profiles);
        foreach (var group in profiles.GroupBy(p => p.Provider))
        {
            var (desktops, observedBy) = Windows(group.Key).Roots();
            foreach (var p in group)
            {
                if (!desktops.TryGetValue(p.DesktopPath, out var pids)) continue;
                if (!roots.TryGetValue(p.CodeKey, out var list)) roots[p.CodeKey] = list = [];
                foreach (int pid in pids) list.Add(new ProcessRoot(pid, 0, observedBy));
            }
        }
        return resources.Sample(roots);
    }

    public bool DesktopAvailable(ProfileProvider provider = ProfileProvider.Claude)
    {
        try { _ = desktopProgram(provider); return true; }
        catch (FileNotFoundException) { return false; }
    }

    internal static bool ValidLink(string text)
    {
        if (text.Length > 16000) return false;
        var match = Regex.Match(text, @"\Aclaude://(?:login/|claude\.ai/(?:magic-link|sso-callback|login)[/?#])([^\s\x00-\x1f]+)\z", RegexOptions.CultureInvariant);
        return match.Success && match.Groups[1].Value.Trim('/', '?', '#', '&', '=').Length > 0;
    }

    public void SendLoginLink(Profile p, string link)
    {
        if (login is not { } attempt || attempt.Key != p.CodeKey || !DesktopWindows.SamePath(attempt.Folder, p.DesktopPath)
            || Environment.TickCount64 >= attempt.Expires)
            throw new InvalidOperationException("Bitte die Anmeldung für dieses Profil erneut starten.");
        if (!ValidLink(link)) throw new ArgumentException("Ungültiger Claude-Anmeldelink.");
        LaunchDesktop(CurrentProfile(p), link);
        login = null;
    }

    public bool IsRunning(Profile p)
    {
        var pids = Windows(p.Provider).Matching(p.DesktopPath);
        lock (colored)
        {
            foreach (int pid in pids)
            {
                if (p.Border && Regex.IsMatch(p.Color, @"\A#[0-9a-fA-F]{6}\z"))
                {
                    if (DesktopWindows.Border(pid, p.Color)) colored.Add(pid);
                }
                else if (colored.Remove(pid)) DesktopWindows.Border(pid, null);
            }
        }
        return pids.Count != 0;
    }

    public bool IsSignedIn(Profile p)
    {
        if (p.Provider != ProfileProvider.Claude) return false;
        try
        {
            if (string.IsNullOrWhiteSpace(p.DesktopPath)) return false;
            var log = Path.Combine(p.DesktopPath, "logs", "main.log");
            if (StoragePaths.RequireUnsharedFile(log))
            {
                try
                {
                    using var stream = File.Open(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stream.Position = Math.Max(0, stream.Length - 2_000_000);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var matches = Regex.Matches(reader.ReadToEnd(), @"Login-state transition \(loggedOut: \w+ → (\w+)");
                    if (matches.Count > 0) return matches[^1].Groups[1].Value == "false";
                }
                catch (IOException) { } // A writer can rotate the log between the check and the read.
            }
            var config = Path.Combine(p.DesktopPath, "config.json");
            if (!StoragePaths.RequireUnsharedFile(config) || new FileInfo(config).Length > 4_000_000) return false;
            using var parsed = JsonDocument.Parse(File.ReadAllBytes(config));
            return parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("lastKnownAccountUuid", out var uuid)
                && uuid.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(uuid.GetString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { return false; }
    }

    public void CloseDesktop(Profile p)
    {
        if (login?.Key == p.CodeKey) login = null;
        var windows = Windows(p.Provider);
        var pids = windows.Matching(p.DesktopPath, fresh: true, strict: true);
        if (pids.Count == 0) return;
        DesktopWindows.Close(pids);
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 3500)
        {
            Thread.Sleep(150);
            if (windows.Matching(p.DesktopPath, fresh: true, strict: true).Count == 0) return;
        }
        throw new IOException($"Bitte {p.Provider} für dieses Profil beenden und den Vorgang erneut starten.");
    }

    public void SignOut(Profile p)
    {
        if (p.Provider != ProfileProvider.Claude) throw new InvalidOperationException("Bitte in Codex abmelden.");
        new Catalog(home).ValidateDesktop(p);
        if (!StoragePaths.RequireUnsharedFile(Path.Combine(p.DesktopPath, "Local State"))
            || !StoragePaths.RequireUnsharedFile(Path.Combine(p.DesktopPath, "config.json")))
            throw new IOException("Der Ordner enthält kein erkennbares Claude-Desktop-Profil.");
        CloseDesktop(p);
        ClearSessionFiles(p.DesktopPath);
    }

    internal static void ClearSessionFiles(string folder)
    {
        string config = Path.Combine(folder, "config.json"), log = Path.Combine(folder, "logs", "main.log");
        if (!StoragePaths.RequireUnsharedFile(config)) throw new IOException("Profilkonfiguration fehlt.");
        var hasLog = StoragePaths.RequireUnsharedFile(log);
        var cookies = new[] { "Cookies", "Cookies-journal", "Cookies-wal", "Cookies-shm" }
            .Select(name => Path.Combine(folder, "Network", name)).Where(StoragePaths.RequireUnsharedFile).ToList();
        var data = JsonNode.Parse(File.ReadAllBytes(config)) as JsonObject ?? throw new InvalidDataException("Ungültige Profilkonfiguration.");
        var keys = data.Select(x => x.Key).Where(k => k == "lastKnownAccountUuid" || k.StartsWith("oauth:", StringComparison.Ordinal)).ToList();
        foreach (var file in cookies) File.Delete(file);
        if (keys.Count > 0)
        {
            foreach (var key in keys) data.Remove(key);
            CodeSharing.AtomicWrite(config, data.ToJsonString());
        }
        if (hasLog) File.AppendAllText(log, $"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} [info] [multi-claude] Login-state transition (loggedOut: false → true)\n");
    }

    public string CodeDirectory(Profile p)
    {
        if (!Regex.IsMatch(p.CodeKey, @"\A[0-9a-f]{32}\z")) throw new ArgumentException("Ungültige Profilkennung.");
        var path = Path.Combine(p.CodeHome, p.Provider == ProfileProvider.Codex ? "codex-home" : "claude-code", p.CodeKey);
        StoragePaths.RequireOrdinary(path);
        return path;
    }

    private void PrepareEnvironment(ProcessStartInfo start, Profile? p)
    {
        if (p?.Provider == ProfileProvider.Codex)
        {
            if (!p.Isolated || p.Sharing) throw new InvalidDataException("Codex benötigt eine eigene Profilkonfiguration.");
            CodexLauncher.Prepare(start, CodeDirectory(p), p.DesktopPath);
            return;
        }
        if (p is not { Isolated: true }) return;
        var config = CodeDirectory(p);
        CodeLauncher.Isolate(start, config);
        if (p.Sharing) CodeSharing.Sync(config, DefaultShared(), true, true);
    }

    public void SetSharing(Profile p, bool enabled)
    {
        if (p.Provider != ProfileProvider.Claude) throw new InvalidOperationException("Diese Freigabe ist nur für Claude Code verfügbar.");
        string dir = CodeDirectory(p);
        CodeLauncher.Prepare(dir);
        CodeSharing.Sync(dir, DefaultShared(), enabled, false);
    }

    private static string DefaultShared() => Path.Combine(CodeLauncher.UserFolder, ".claude");

    private ProcessStartInfo CodeStart(Profile? p, bool login)
    {
        if (login && p is not { Isolated: true }) throw new ArgumentException("Für die Anmeldung ein isoliertes Claude-Code-Profil wählen.");
        if (p?.Provider == ProfileProvider.Codex) p = CurrentProfile(p);
        var provider = p?.Provider ?? ProfileProvider.Claude;
        var start = CodeLauncher.StartInfo(terminalProgram(provider));
        if (login)
        {
            if (provider == ProfileProvider.Claude) start.ArgumentList.Add("auth");
            start.ArgumentList.Add("login");
        }
        PrepareEnvironment(start, p);
        return start;
    }

    public void OpenCode(Profile p, bool login = false) => OpenCode(p, CodeStart(p, login));

    internal void OpenCode(Profile p, ProcessStartInfo start)
    {
        start.WorkingDirectory = CodeLauncher.UserFolder;
        CodeProcessRegistry.Record(home, p, CodeLauncher.NewConsole(start));
    }

    public int RunTerminal(Profile? p, bool login, uint? consolePid)
    {
        var start = CodeStart(p, login);
        CodeLauncher.Attach(consolePid);
        return RunTerminal(p, start);
    }

    internal int RunTerminal(Profile? p, ProcessStartInfo start)
    {
        using var cli = Process.Start(start) ?? throw new IOException("Das Profil-Terminal konnte nicht gestartet werden.");
        CodeProcessRegistry.Record(home, p, CodeProcessRegistry.Identity(cli.Id, cli.SafeHandle));
        cli.WaitForExit();
        return cli.ExitCode;
    }

    public string CodeCommand(Profile p)
    {
        if (p.Isolated) _ = CodeDirectory(p);
        string args = "--code-terminal " + (p.Isolated ? p.CodeKey : "shared") + " --manager-home " + CodeLauncher.QuoteArg(home);
        return "$LASTEXITCODE=(Start-Process -Wait -PassThru -NoNewWindow -FilePath " + CodeLauncher.PsLiteral(Environment.ProcessPath!)
            + " -WorkingDirectory $PWD -ArgumentList (" + CodeLauncher.PsLiteral(args) + "+' --console-pid '+$PID)).ExitCode";
    }

    public void SaveShortcut(Profile p, string destination)
    {
        _ = CodeDirectory(p);
        ProfileIcons.SaveShortcut(home, p, destination);
    }
}
