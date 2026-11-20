using System.Drawing;
using System.Runtime.InteropServices;

namespace MultiClaude;

// Uncombined taskbar buttons use the live window icons. WM_SETICON borrows the HICON.
internal sealed class TaskbarWindowIcons : IDisposable
{
    private sealed record Entry(string ProfileKey, int Pid, string File, int Size, Icon Icon, nint Original)
    {
        internal Entry? Previous;
        internal bool RestorePending;
    }
    private readonly Dictionary<(nint Window, int Kind), Entry> entries = [];
    private const uint GetIcon = 0x7f, SetIcon = 0x80;

    internal void Apply(nint window, int pid, string profileKey, string file)
    {
        if (!OwnedBy(window, pid)) return;
        uint dpi = GetDpiForWindow(window);
        for (int kind = 0; kind <= 1; kind++)
        {
            int size = GetSystemMetricsForDpi(kind == 0 ? 49 /* SM_CXSMICON */ : 11 /* SM_CXICON */, dpi);
            if (size <= 0 || !Send(window, GetIcon, kind, 0, out var current)) continue;
            var key = (window, kind);
            var old = Resolve(key, current);
            bool stillApplied = old != null && old.Pid == pid && old.Icon.Handle == current;
            if (stillApplied && old!.File == file && old.Size == size) continue;

            if (!OwnedBy(window, pid)) return;
            var icon = Icon.ExtractIcon(file, 0, size);
            if (icon == null) continue;
            nint original = stillApplied ? old!.Original : current;
            if (Send(window, SetIcon, kind, icon.Handle, out var replaced))
            {
                entries[key] = new(profileKey, pid, file, size, icon, replaced == current ? original : replaced);
                old?.Icon.Dispose();
            }
            // A timeout can occur after the receiver starts handling WM_SETICON. Keep both
            // handles until WM_GETICON tells us which one the window actually adopted.
            else entries[key] = new(profileKey, pid, file, size, icon, original) { Previous = old };
        }
    }

    private Entry? Resolve((nint Window, int Kind) key, nint current)
    {
        if (!entries.TryGetValue(key, out var entry) || entry.Previous is not { } previous) return entry;
        entry.Previous = null;
        if (current == entry.Icon.Handle) previous.Icon.Dispose();
        else
        {
            entry.Icon.Dispose();
            entries[key] = entry = previous;
        }
        return entry;
    }

    internal void Prune()
    {
        foreach (var (key, entry) in entries.ToArray())
            if (!OwnedBy(key.Window, entry.Pid)) Release(key, entry);
            else if (entry.RestorePending) Restore(key, entry);
    }

    internal void RestoreProfile(string? profileKey)
    {
        foreach (var (key, entry) in entries.ToArray())
            if (profileKey == null || entry.ProfileKey == profileKey) Restore(key, entry);
    }

    private void Restore((nint Window, int Kind) key, Entry entry)
    {
        if (OwnedBy(key.Window, entry.Pid))
        {
            entry.RestorePending = true;
            if (!Send(key.Window, GetIcon, key.Kind, 0, out var current)) return;
            entry = Resolve(key, current)!;
            entry.RestorePending = true;
            if (current == entry.Icon.Handle && !Send(key.Window, SetIcon, key.Kind, entry.Original, out _)) return;
        }
        Release(key, entry);
    }

    private void Release((nint Window, int Kind) key, Entry entry)
    {
        entries.Remove(key);
        entry.Previous?.Icon.Dispose();
        entry.Icon.Dispose();
    }

    public void Dispose() => RestoreProfile(null);

    private static bool OwnedBy(nint window, int pid)
    {
        GetWindowThreadProcessId(window, out uint owner);
        return owner == pid;
    }

    private static bool Send(nint window, uint message, int kind, nint icon, out nint result) =>
        SendMessageTimeoutW(window, message, kind, icon, 0x22 /* ABORTIFHUNG | ERRORONEXIT */, 200, out result) != 0;

    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wp, nint lp, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
