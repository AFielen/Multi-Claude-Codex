using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace MultiClaude;

public partial class MainWindow
{
    private Forms.NotifyIcon? managerTray;
    internal Forms.ContextMenuStrip ManagerMenu { get; }
    private WindowState restoreState = WindowState.Normal;
    private bool exitRequested;

    // Native confirmations and file pickers disable the HWND outside WPF's OwnedWindows collection.
    internal bool ModalOpen => !IsEnabled || OwnedWindows.Cast<Window>().Any(w => w.IsVisible)
        || !IsWindowEnabled(new WindowInteropHelper(this).Handle);

    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);

    private void InitializeManagerTray()
    {
        managerTray = new Forms.NotifyIcon
        {
            Icon = new Icon(typeof(MainWindow), "Resources.Manager.ico"),
            Text = Title,
            ContextMenuStrip = ManagerMenu
        };
        managerTray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowManager(); };
        managerTray.Visible = true;
    }

    private Forms.ContextMenuStrip CreateManagerMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            // WinForms initially cancels opening an empty strip; we supply its items here.
            e.Cancel = closed;
            if (closed) return;
            foreach (var old in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) old.Dispose();
            menu.Items.Add(T("show_manager"), null, (_, _) => ShowManager());
            menu.Items.Add(new Forms.ToolStripSeparator());
            bool available = !ModalOpen;
            if (profiles.Count == 0)
                menu.Items.Add(new Forms.ToolStripMenuItem(T("nenhum")) { Enabled = false });
            foreach (var profile in profiles)
            {
                string key = profile.CodeKey;
                bool running = status.GetValueOrDefault(key).Open;
                var entry = new Forms.ToolStripMenuItem(ProfileLabel(profile).Replace("&", "&&")) { Enabled = available };
                entry.DropDownItems.Add(T(running ? "switch" : "abrir"), null, (_, _) =>
                {
                    if (!closed && !ModalOpen) ActivateProfile(key);
                });
                entry.DropDownItems.Add(T("fechar"), null, (_, _) => Safe(() =>
                {
                    if (!closed && !ModalOpen && Catalog.FindInExisting(catalog.Home, key) is { } current)
                        CloseProfile(current);
                })).Enabled = running;
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(T("exit_manager"), null, (_, _) => ExitManager()).Enabled = available;
        };
        return menu;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized) restoreState = WindowState;
        else if (managerTray is { Visible: true } && !ModalOpen) HideManager();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || exitRequested || managerTray is not { Visible: true }) return;
        e.Cancel = true;
        // WPF permits changing visibility once the canceled Closing event has returned.
        Dispatcher.InvokeAsync(() => { if (!closed && !ModalOpen) HideManager(); });
    }

    private void HideManager()
    {
        Hide();
        usageRead?.Cancel();
    }

    internal void ShowManager()
    {
        if (closed) return;
        bool wasHidden = !IsVisible;
        if (WindowState == WindowState.Minimized) WindowState = restoreState;
        if (wasHidden)
        {
            Show();
            RefreshAfterTrayRestore();
        }
        var target = OwnedWindows.Cast<Window>().LastOrDefault(w => w.IsVisible) ?? this;
        var hwnd = new WindowInteropHelper(target).Handle;
        if (IsWindowEnabled(hwnd)) target.Activate();
        else SetForegroundWindow(GetWindow(hwnd, 6)); // GW_ENABLEDPOPUP also handles a native picker owned by a WPF dialog.
    }

    internal void ExitManager()
    {
        if (closed || ModalOpen) return;
        exitRequested = true;
        Close();
    }

    private void DisposeManagerTray()
    {
        if (managerTray == null) return;
        var icon = managerTray.Icon;
        managerTray.Dispose();
        ManagerMenu.Dispose();
        icon?.Dispose();
        managerTray = null;
    }
}
