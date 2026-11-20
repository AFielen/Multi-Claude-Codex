using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Forms;

namespace MultiClaude;

public sealed class ProfileBindings : IDisposable
{
    private sealed record Binding(string Text, int Token);
    private sealed record TrayEntry(string Name, string Color, NotifyIcon Notification, Icon Icon) : IDisposable
    {
        public void Dispose()
        {
            Notification.Visible = false;
            Notification.Dispose();
            Icon.Dispose();
        }
    }

    private readonly Dictionary<long, Binding> bindings = [];
    private readonly Dictionary<int, long> owners = [];
    private readonly Dictionary<long, TrayEntry> trays = [];
    private readonly Action<long> activate;
    private readonly HwndSource source;
    private readonly nint handle;
    private int next = 1;
    private bool disposed;

    public bool IsHotkeyRegistered(long id) => bindings.ContainsKey(id);
    public bool IsTrayRegistered(long id) => trays.ContainsKey(id);

    public ProfileBindings(Window window, Action<long> activate)
    {
        this.activate = activate;
        handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Windows-Fenster fehlt.");
        source.AddHook(OnMessage);
        window.Closed += (_, _) => Dispose();
    }

    public static string NormalizeShortcut(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var components = value.Split('+').Select(part => part.Trim().ToUpperInvariant()).ToArray();
        var mods = new HashSet<string>();
        string error = "Strg oder Alt, optional Umschalt, mit einer Ziffer, einem Buchstaben oder F1–F24 (außer F12) kombinieren.";
        foreach (string part in components[..^1])
        {
            var modifier = part switch
            {
                "STRG" or "CONTROL" or "CTRL" => "Ctrl",
                "ALT" => "Alt",
                "UMSCHALT" or "SHIFT" => "Shift",
                _ => throw new ArgumentException(error)
            };
            if (!mods.Add(modifier)) throw new ArgumentException(error);
        }
        var key = components[^1];
        if ((!mods.Contains("Alt") && !mods.Contains("Ctrl")) || key == "F12" ||
            !Regex.IsMatch(key, @"\A(?:[A-Z0-9]|F(?:[1-9]|1[0-9]|2[0-4]))\z"))
            throw new ArgumentException(error);
        return string.Join("+", new[] { "Ctrl", "Alt", "Shift" }.Where(mods.Contains).Append(key));
    }

    public void SetHotkey(long id, string? shortcut)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var text = NormalizeShortcut(shortcut);
        bindings.TryGetValue(id, out var old);
        if (old?.Text == text) return;
        Binding? binding = null;
        if (text != "")
        {
            if (bindings.Values.Any(b => b.Text == text))
                throw new InvalidOperationException("Dieses Tastenkürzel ist bereits einem anderen Profil zugeordnet.");
            if (next > 0xbfff) throw new InvalidOperationException("Bitte den Manager vor weiteren Kürzeländerungen neu starten.");
            var parts = text.Split('+');
            uint modifiers = 0x4000;
            foreach (string mod in parts[..^1]) modifiers |= mod switch { "Ctrl" => 2u, "Alt" => 1u, _ => 4u };
            var key = parts[^1];
            uint vk = key.Length == 1 ? key[0] : (uint)(0x70 + int.Parse(key[1..]) - 1);
            int token = next++;
            if (!RegisterHotKey(handle, token, modifiers, vk))
                throw new InvalidOperationException("Windows konnte das Tastenkürzel " + text + " nicht registrieren.", new Win32Exception(Marshal.GetLastWin32Error()));
            binding = new(text, token);
        }
        if (old != null)
        {
            if (!UnregisterHotKey(handle, old.Token))
            {
                if (binding != null) UnregisterHotKey(handle, binding.Token);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            owners.Remove(old.Token);
            bindings.Remove(id);
        }
        if (binding != null)
        {
            owners[binding.Token] = id;
            bindings[id] = binding;
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        int token = (int)wparam;
        if (message == 0x0312 && owners.ContainsKey(token))
        {
            handled = true;
            source.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (owners.TryGetValue(token, out var id)) activate(id);
            });
        }
        return 0;
    }

    public IReadOnlyList<string> Sync(IEnumerable<Profile> profiles)
    {
        var all = profiles.ToDictionary(p => p.Id);
        var errors = new List<string>();
        foreach (var id in bindings.Keys.Where(id => !all.ContainsKey(id)).ToArray()) SetHotkey(id, "");
        foreach (var id in trays.Keys.Where(id => !all.TryGetValue(id, out var p) || !p.Tray).ToArray())
        {
            trays[id].Dispose();
            trays.Remove(id);
        }
        foreach (var p in all.Values)
        {
            try { SetHotkey(p.Id, p.Shortcut); }
            catch (Exception err) { errors.Add(p.Name + " · " + err.Message); }
            if (!p.Tray) continue;
            if (trays.TryGetValue(p.Id, out var existing) && existing.Name == p.Name && existing.Color == p.Color) continue;
            try
            {
                var icon = ProfileIcons.Tray(p);
                var notify = new NotifyIcon();
                try
                {
                    notify.Icon = icon;
                    notify.Text = p.Name[..Math.Min(127, p.Name.Length)];
                    notify.MouseClick += (_, e) =>
                    {
                        if (e.Button == MouseButtons.Left && trays.TryGetValue(p.Id, out var live) && live.Notification == notify)
                            activate(p.Id);
                    };
                    notify.Visible = true;
                }
                catch { notify.Dispose(); icon.Dispose(); throw; }
                existing?.Dispose();
                trays[p.Id] = new(p.Name, p.Color, notify, icon);
            }
            catch (Exception err) { errors.Add(p.Name + " · " + err.Message); }
        }
        return errors;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var entry in trays.Values) entry.Dispose();
        trays.Clear();
        foreach (var entry in bindings.Values) UnregisterHotKey(handle, entry.Token);
        bindings.Clear();
        owners.Clear();
        if (!source.IsDisposed) source.RemoveHook(OnMessage);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
