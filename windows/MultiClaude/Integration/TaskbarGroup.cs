using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MultiClaude;

internal sealed class TaskbarGroup
{
    internal const uint RelaunchCommand = 2, RelaunchIcon = 3, RelaunchName = 4, AppId = 5;
    private const uint PreventPinning = 9;
    private const int MaxPropertyText = 255;
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private readonly Dictionary<nint, WindowState> windows = [];

    private readonly record struct Value(VarEnum Type, string? Text = null, bool Flag = false)
    {
        internal static Value String(string text) => new(VarEnum.VT_LPWSTR, text);
    }

    private sealed record WindowState(int Pid, string ProfileKey, string GroupId)
    {
        internal readonly Dictionary<uint, (Value Original, Value Assigned)> Changes = [];
    }

    internal bool Apply(nint hwnd, int pid, string home, Profile profile, string iconFile)
    {
        if (!OwnsWindow(pid, hwnd) || Environment.ProcessPath is not string manager) return false;
        if (Open(hwnd, out var store) < 0 || store == null) return false;
        try
        {
            string catalog = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)).ToUpperInvariant();
            string id = "RichardKPL.MultiClaude.Profile." +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog + "\n" + profile.CodeKey)));
            if (!windows.TryGetValue(hwnd, out var state) || state.Pid != pid || state.GroupId != id)
                windows[hwnd] = state = new WindowState(pid, profile.CodeKey, id);
            string Command(string program, string folder) => CodeLauncher.QuoteArg(program) + " --open-profile " +
                profile.CodeKey + " --manager-home " + CodeLauncher.QuoteArg(folder);
            string command = Command(manager, home);
            if (command.Length > MaxPropertyText)
            {
                string shortManager = ShortPath(manager);
                command = Command(shortManager, home);
                if (command.Length > MaxPropertyText) command = Command(shortManager, ShortPath(home));
            }
            string name = "Claude · " + profile.Name;
            string icon = (iconFile.Length + 2 > MaxPropertyText ? ShortPath(iconFile) : iconFile) + ",0";
            bool changed = false;
            bool canRelaunch = command.Length <= MaxPropertyText && Set(store, state, RelaunchCommand, Value.String(command), ref changed)
                && Set(store, state, RelaunchName, Value.String(name[..Math.Min(name.Length, MaxPropertyText)]), ref changed);
            // A deep installation without short path aliases may exceed the Shell's window property limit.
            // Keep its separate group and avoid a pinned shortcut that would open the wrong account.
            if (!canRelaunch && !Set(store, state, PreventPinning, new Value(VarEnum.VT_BOOL, Flag: true), ref changed)) return false;
            // Explorer reads relaunch metadata when the window joins its new group.
            if (icon.Length <= MaxPropertyText) Set(store, state, RelaunchIcon, Value.String(icon), ref changed);
            return Set(store, state, AppId, Value.String(id), ref changed, force: changed);
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    internal void Prune()
    {
        foreach (var (hwnd, state) in windows)
            if (!OwnsWindow(state.Pid, hwnd)) windows.Remove(hwnd);
    }

    internal void RestoreProfile(Profile profile) => Restore(profile.CodeKey);
    internal void RestoreAll() => Restore(null);

    private void Restore(string? profileKey)
    {
        foreach (var (hwnd, state) in windows)
        {
            if (profileKey != null && state.ProfileKey != profileKey) continue;
            windows.Remove(hwnd);
            if (!OwnsWindow(state.Pid, hwnd) || Open(hwnd, out var store) < 0 || store == null) continue;
            try
            {
                if (Get(store, AppId)?.Text != state.GroupId) continue;
                // Restore the old group last so Explorer sees its original relaunch information.
                foreach (var (id, change) in state.Changes.OrderBy(pair => pair.Key == AppId))
                    if (Get(store, id) == change.Assigned) Write(store, id, change.Original);
            }
            finally { Marshal.ReleaseComObject(store); }
        }
    }

    private static bool OwnsWindow(int pid, nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint owner);
        return pid > 0 && owner == pid;
    }

    internal static string? Read(nint hwnd, uint propertyId)
    {
        if (Open(hwnd, out var store) < 0 || store == null) return null;
        try { return Get(store, propertyId)?.Text; }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static int Open(nint hwnd, out IPropertyStore? store)
    {
        var iid = typeof(IPropertyStore).GUID;
        return SHGetPropertyStoreForWindow(hwnd, in iid, out store);
    }

    private static Value? Get(IPropertyStore store, uint id)
    {
        var key = new PropertyKey(id);
        int result = store.GetValue(in key, out var value);
        try
        {
            if (result < 0) return null;
            return (VarEnum)value.Type switch
            {
                VarEnum.VT_EMPTY => new Value(VarEnum.VT_EMPTY),
                VarEnum.VT_LPWSTR => Value.String(Marshal.PtrToStringUni(value.Data) ?? ""),
                VarEnum.VT_BOOL => new Value(VarEnum.VT_BOOL, Flag: (short)value.Data != 0),
                _ => null
            };
        }
        finally { PropVariantClear(ref value); }
    }

    private static string ShortPath(string path)
    {
        var result = new StringBuilder(path.Length + 1);
        uint size = GetShortPathNameW(path, result, (uint)result.Capacity);
        if (size >= result.Capacity)
        {
            result.EnsureCapacity((int)size);
            size = GetShortPathNameW(path, result, (uint)result.Capacity);
        }
        return size > 0 && size < result.Capacity ? result.ToString() : path;
    }

    private static bool Set(IPropertyStore store, WindowState state, uint id, Value assigned, ref bool changed, bool force = false)
    {
        if (Get(store, id) is not Value current) return false;
        if (!force && current == assigned) return true;
        if (!Write(store, id, assigned)) return false;
        if (current != assigned)
        {
            var original = state.Changes.TryGetValue(id, out var previous) && previous.Assigned == current ? previous.Original : current;
            state.Changes[id] = (original, assigned);
            changed = true;
        }
        return true;
    }

    private static bool Write(IPropertyStore store, uint id, Value replacement)
    {
        var key = new PropertyKey(id);
        var value = new PropVariant { Type = (ushort)replacement.Type };
        if (replacement.Type == VarEnum.VT_LPWSTR) value.Data = Marshal.StringToCoTaskMemUni(replacement.Text);
        if (replacement.Type == VarEnum.VT_BOOL) value.Data = replacement.Flag ? -1 : 0;
        try { return store.SetValue(in key, in value) >= 0; }
        finally { PropVariantClear(ref value); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(uint id)
    {
        private readonly Guid format = AppUserModel;
        private readonly uint propertyId = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        internal ushort Type;
        private ushort reserved1, reserved2, reserved3;
        internal nint Data;
        private nint extra; // The native union occupies two pointer widths.
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder result, uint capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
