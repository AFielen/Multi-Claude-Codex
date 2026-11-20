using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MultiClaude;

internal static class CodeProcessRegistry
{
    // The handle is the one returned by process creation. A fast exit cannot turn it into another process.
    internal static ProcessRoot? Identity(int pid, SafeProcessHandle handle) =>
        GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
            ? new ProcessRoot(pid, DateTime.FromFileTimeUtc(creation).Ticks) : null;

    internal static void Record(string home, Profile? profile, ProcessRoot? identity)
    {
        if (profile is not { Isolated: true } || identity is not { } root) return;
        string file = Path.Combine(home, "perfis.db");
        try
        {
            if (!File.Exists(file)) return;
            using var db = new Sqlite(file);
            db.Transaction(() =>
            {
                db.Run("""
                    CREATE TABLE IF NOT EXISTS code_processes (
                        pid INTEGER NOT NULL, started_utc_ticks INTEGER NOT NULL,
                        code_key TEXT NOT NULL REFERENCES profile_options(code_key) ON DELETE CASCADE,
                        PRIMARY KEY(pid, started_utc_ticks))
                    """);
                db.Run("""
                    INSERT OR IGNORE INTO code_processes(pid, started_utc_ticks, code_key)
                    SELECT ?, ?, code_key FROM profile_options WHERE profile=? AND code_key=? AND isolated=1
                    """, root.Pid, root.StartedUtcTicks, profile.Id, profile.CodeKey);
                return 0;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // Monitoring is optional.
    }

    internal static Dictionary<string, List<ProcessRoot>> Active(string home, IReadOnlyList<Profile> profiles) =>
        Active(home, profiles, IsCurrent);

    internal static Dictionary<string, List<ProcessRoot>> Active(string home, IReadOnlyList<Profile> profiles,
        Func<ProcessRoot, bool?> isCurrent)
    {
        var active = new Dictionary<string, List<ProcessRoot>>(StringComparer.Ordinal);
        var keys = profiles.Where(p => p.Isolated).Select(p => p.CodeKey).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0) return active;
        string file = Path.Combine(home, "perfis.db");
        try
        {
            using var db = new Sqlite(file, readOnly: true);
            if (db.Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name='code_processes'") == null)
                return active;
            var expired = new List<ProcessRoot>();
            foreach (var row in db.Query("SELECT pid, started_utc_ticks, code_key FROM code_processes"))
            {
                if (!int.TryParse(row[0], out int pid) || pid <= 0 || !long.TryParse(row[1], out long started)
                    || row[2] is not { } key) continue;
                var root = new ProcessRoot(pid, started);
                bool? alive = isCurrent(root);
                if (alive == false) expired.Add(root);
                else if (keys.Contains(key)) // The sampler needs inaccessible candidates to flag incomplete readings.
                {
                    if (!active.TryGetValue(key, out var list)) active.Add(key, list = []);
                    list.Add(root);
                }
            }
            if (expired.Count > 0)
            {
                using var writer = new Sqlite(file);
                writer.Transaction(() =>
                {
                    foreach (var root in expired)
                        writer.Run("DELETE FROM code_processes WHERE pid=? AND started_utc_ticks=?", root.Pid, root.StartedUtcTicks);
                    return 0;
                });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return active;
    }

    // Access denied or a transient query failure leaves the registration for a later sample.
    private static bool? IsCurrent(ProcessRoot root)
    {
        const uint queryLimitedInformation = 0x1000, synchronize = 0x100000;
        using var handle = OpenProcess(queryLimitedInformation | synchronize, false, root.Pid);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error() == 87 ? false : null; // ERROR_INVALID_PARAMETER: no such PID.
        return WaitForSingleObject(handle, 0) switch
        {
            0 => false,
            258 when Identity(root.Pid, handle) is { } actual => actual == root,
            _ => null
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
}
