using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiClaude;

internal sealed class DesktopWindows(ProfileProvider provider = ProfileProvider.Claude)
{
    private readonly object gate = new();
    private Dictionary<string, List<int>> snapshot = new(StringComparer.OrdinalIgnoreCase);
    private long readAt = -10_000;
    private long observedByUtcTicks;

    internal IReadOnlySet<string> Folders() => Roots().ByFolder.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal (IReadOnlyDictionary<string, List<int>> ByFolder, long ObservedByUtcTicks) Roots()
    {
        lock (gate)
        {
            Refresh(false, false);
            return (snapshot, observedByUtcTicks);
        }
    }

    internal IReadOnlyList<int> Matching(string folder, bool fresh = false, bool strict = false)
    {
        lock (gate)
        {
            Refresh(fresh, strict);
            return snapshot.TryGetValue(folder, out var pids) ? pids : [];
        }
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

    internal static bool Border(int pid, string? hex)
    {
        uint color = 0xffffffff;
        if (hex != null)
        {
            var bytes = Convert.FromHexString(hex[1..]);
            color = (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);
        }
        bool ok = false;
        foreach (var hwnd in Windows([pid]))
            if (Pid(hwnd) == pid) ok |= DwmSetWindowAttribute(hwnd, 34, ref color, 4) == 0;
        return ok;
    }

    internal static void Close(IReadOnlyList<int> pids)
    {
        foreach (var hwnd in Windows(pids)) if (pids.Contains(Pid(hwnd))) PostMessageW(hwnd, 0x10, 0, 0);
    }

    private delegate bool EnumProc(nint hwnd, nint data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint ptr);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint param, nint data);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
}
