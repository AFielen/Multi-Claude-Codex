using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiClaude;

internal sealed class DesktopWindows(ProfileProvider provider = ProfileProvider.Claude) : IDisposable
{
    private readonly object gate = new();
    private readonly TaskbarGroup taskbarGroups = new();
    private readonly TaskbarWindowIcons taskbarIcons = new();
    private Dictionary<string, List<int>> snapshot = new(StringComparer.OrdinalIgnoreCase);
    private long readAt = -10_000;
    private long observedByUtcTicks;

    internal IReadOnlySet<string> Folders() => Roots().ByFolder.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal (IReadOnlyDictionary<string, List<int>> ByFolder, long ObservedByUtcTicks) Roots(bool fresh = false, bool strict = false)
    {
        lock (gate)
        {
            Refresh(fresh, strict);
            return (snapshot, observedByUtcTicks);
        }
    }

    internal IReadOnlyList<int> Matching(string folder, bool fresh = false, bool strict = false)
    {
        var (byFolder, _) = Roots(fresh, strict);
        return byFolder.TryGetValue(folder, out var pids) ? pids : [];
    }

    private void Refresh(bool fresh, bool strict)
    {
        if (!fresh && Environment.TickCount64 - readAt < 1500) return;
        var byFolder = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        // WMI supplies command lines through its supported process provider. No debugging or process-memory access.
        using var current = Process.GetCurrentProcess();
        string executable = provider == ProfileProvider.Codex ? "(Name='ChatGPT.exe' OR Name='Codex.exe')" : "Name='claude.exe'";
        string query = $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE {executable} AND SessionId={current.SessionId} AND (CommandLine LIKE '%--user-data-dir=%' OR CommandLine IS NULL)";
        // A process born after this boundary cannot safely inherit a cached profile-to-PID association.
        long beforeQuery = DateTime.UtcNow.Ticks;
        dynamic? locator = null, service = null, records = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!)!;
            service = locator.ConnectServer(".", @"root\cimv2");
            records = service.ExecQuery(query);
            foreach (dynamic row in records)
            {
                try
                {
                    string? command = Property(row, "CommandLine") as string;
                    if (string.IsNullOrEmpty(command))
                    {
                        if (strict) throw new IOException($"Windows konnte eine laufende {provider}-Instanz nicht zuordnen.");
                        continue;
                    }
                    string? folder = FolderFromCommand(command, strict);
                    if (folder == null) continue;
                    if (!byFolder.TryGetValue(folder, out var pids)) byFolder[folder] = pids = [];
                    pids.Add(Convert.ToInt32(Property(row, "ProcessId")));
                }
                finally { Marshal.FinalReleaseComObject(row); }
            }
        }
        finally
        {
            if (records != null) Marshal.FinalReleaseComObject(records);
            if (service != null) Marshal.FinalReleaseComObject(service);
            if (locator != null) Marshal.FinalReleaseComObject(locator);
        }
        snapshot = byFolder;
        observedByUtcTicks = beforeQuery;
        readAt = Environment.TickCount64;
    }

    private static object? Property(dynamic row, string name)
    {
        // SWbemObject exposes provider properties through its SWbemPropertySet.
        dynamic properties = row.Properties_;
        dynamic property = properties.Item(name);
        try { return property.Value; }
        finally { Marshal.FinalReleaseComObject(property); Marshal.FinalReleaseComObject(properties); }
    }

    internal static string? FolderFromCommand(string command, bool strict)
    {
        var argv = CommandLineToArgvW(command, out int count);
        if (argv == 0) throw new IOException("Windows konnte die Desktop-Befehlszeile nicht lesen.");
        try
        {
            string? path = null;
            int matches = 0;
            for (int i = 1; i < count; i++)
            {
                string arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
                if (arg.StartsWith("--type=", StringComparison.Ordinal)) return null;
                const string prefix = "--user-data-dir=";
                if (arg.StartsWith(prefix, StringComparison.Ordinal))
                {
                    path = StoragePaths.Resolve(arg[prefix.Length..]);
                    matches++;
                }
            }
            if (matches == 1 && !string.IsNullOrEmpty(path)) return path;
            if (matches > 0 && strict) throw new IOException("Windows konnte den Desktop-Profilpfad nicht eindeutig bestimmen.");
            return null;
        }
        finally { LocalFree(argv); }
    }

    internal static bool SamePath(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static List<nint> Windows(IEnumerable<int> pids)
    {
        var wanted = pids.ToHashSet();
        var result = new List<nint>();
        if (wanted.Count == 0) return result;
        EnumWindows((hwnd, _) =>
        {
            if (wanted.Contains(Pid(hwnd)) && IsWindowVisible(hwnd) && (GetWindowLongW(hwnd, -20) & 0x80) == 0)
                result.Add(hwnd);
            return true;
        }, 0);
        return result;
    }

    private static int Pid(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    internal static bool Focus(IReadOnlyList<int> pids)
    {
        bool blocked = false;
        foreach (var hwnd in Windows(pids))
        {
            if (!pids.Contains(Pid(hwnd))) continue;
            blocked = true;
            if (!IsWindowEnabled(hwnd)) continue;
            if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
            if (SetForegroundWindow(hwnd)) return true;
        }
        if (blocked) throw new IOException("Windows hat den Fensterwechsel blockiert. Bitte das Profilfenster über die Taskleiste öffnen.");
        return false;
    }

    private static bool IsTaskbarWindow(nint hwnd)
    {
        const int appWindow = 0x40000, noActivate = 0x08000000;
        int style = GetWindowLongW(hwnd, -20);
        return (style & appWindow) != 0 || ((style & noActivate) == 0 && GetWindow(hwnd, 4 /* GW_OWNER */) == 0);
    }

    internal void PruneIcons()
    {
        taskbarIcons.Prune();
        taskbarGroups.Prune();
    }

    internal void RestoreProfile(Profile profile)
    {
        taskbarIcons.RestoreProfile(profile.CodeKey);
        taskbarGroups.RestoreProfile(profile);
    }

    public void Dispose()
    {
        taskbarIcons.Dispose();
        taskbarGroups.RestoreAll();
    }

    internal bool UpdateAppearance(string home, Profile profile, int pid, long observedByUtcTicks, bool resetBorder)
    {
        var handles = Windows([pid]);
        if (handles.Count == 0) return false;
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid || CodeProcessRegistry.Identity(pid, process) is not { } identity
            || identity.StartedUtcTicks > observedByUtcTicks) return false;
        var bytes = Convert.FromHexString(profile.Color[1..]);
        uint border = profile.Border ? (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16) : 0xffffffff;
        bool borderSet = false;
        foreach (var hwnd in handles)
            if (Pid(hwnd) == pid && (profile.Border || resetBorder))
                borderSet |= DwmSetWindowAttribute(hwnd, 34, ref border, 4) == 0;

        var taskbarWindows = handles.Where(IsTaskbarWindow).ToArray();
        if (taskbarWindows.Length > 0)
        {
            int length = 32768;
            var executable = new StringBuilder(length);
            if (QueryFullProcessImageNameW(process, 0, executable, ref length))
            {
                string icon = ProfileIcons.TaskbarIcon(home, executable.ToString(), profile.Color);
                foreach (var hwnd in taskbarWindows)
                {
                    taskbarGroups.Apply(hwnd, pid, home, profile, icon);
                    taskbarIcons.Apply(hwnd, pid, profile.CodeKey, icon);
                }
            }
        }
        return profile.Border && borderSet;
    }

    internal void CloseProfile(string folder)
    {
        var (roots, observedBy) = Roots(fresh: true, strict: true);
        if (!roots.TryGetValue(folder, out var pids)) return;
        var targets = new List<ProcessRoot>();
        foreach (int pid in pids)
        {
            using var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (handle.IsInvalid || CodeProcessRegistry.Identity(pid, handle) is not { } identity
                || identity.StartedUtcTicks > observedBy) continue;
            targets.Add(identity);
            foreach (var hwnd in Windows([pid]))
                if (Pid(hwnd) == pid) PostMessageW(hwnd, 0x10 /* WM_CLOSE */, 0, 0);
        }

        var timer = Stopwatch.StartNew();
        while (true)
        {
            var remaining = Matching(folder, fresh: true, strict: true);
            if (remaining.Count == 0) return;
            // Electron may hide its last window in the tray. Leave visible prompts to the user.
            if (targets.Count > 0 && Windows(remaining).Count == 0)
            {
                try { DesktopShutdown.Request(targets.Where(p => remaining.Contains(p.Pid)).ToArray()); }
                catch (Win32Exception) when (Matching(folder, fresh: true, strict: true).Count == 0) { return; }
                targets.Clear();
                continue;
            }
            if (timer.ElapsedMilliseconds >= 3500)
                throw new IOException($"Bitte {provider} für dieses Profil beenden und den Vorgang erneut starten.");
            Thread.Sleep(150);
        }
    }

    private delegate bool EnumProc(nint hwnd, nint data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint ptr);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint param, nint data);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
}
