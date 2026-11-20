using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiClaude;

internal static class DesktopShutdown
{
    // RM_UNIQUE_PROCESS contains a DWORD and a FILETIME, both aligned on four bytes.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct UniqueProcess(int Pid, long StartFileTime);

    internal static void Request(IReadOnlyList<ProcessRoot> roots)
    {
        if (roots.Count == 0) return;
        var targets = roots.Select(p => new UniqueProcess(p.Pid,
            new DateTime(p.StartedUtcTicks, DateTimeKind.Utc).ToFileTimeUtc())).ToArray();
        Check(RmStartSession(out uint session, 0, new StringBuilder(33)));
        try
        {
            // Register only these process identities. Other profiles may use the same executable.
            Check(RmRegisterResources(session, 0, 0, (uint)targets.Length, targets, 0, 0));
            Check(RmShutdown(session, 0 /* cooperative shutdown */, 0));
        }
        finally { RmEndSession(session); }
    }

    private static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount, nint files, uint processCount,
        [In] UniqueProcess[] processes, uint serviceCount, nint services);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmShutdown(uint session, uint flags, nint callback);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmEndSession(uint session);
}
