using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using MultiClaude;

internal sealed class ManagerWarningMonitor : IDisposable
{
    private readonly Timer timer;
    private string? warning;

    internal ManagerWarningMonitor(MainWindow window)
    {
        nint manager = new WindowInteropHelper(window).Handle;
        string title = Texts.Get("de", "erro");
        timer = new Timer(_ =>
        {
            nint popup = GetWindow(manager, 6 /* GW_ENABLEDPOPUP */);
            if (popup == 0 || popup == manager || GetWindow(popup, 4 /* GW_OWNER */) != manager ||
                GetDlgItem(popup, 1 /* IDOK */) == 0 || ReadText(popup) != title) return;

            var messages = new List<string>();
            nint child = 0;
            while ((child = FindWindowExW(popup, child, "Static", null)) != 0)
                if (ReadText(child) is { Length: > 0 } message) messages.Add(message);
            string detail = $"Manager displayed {title}: {string.Join(" ", messages)}";
            if (Interlocked.CompareExchange(ref warning, detail, null) == null) Console.Error.WriteLine(detail);
            PostMessageW(popup, 0x0111 /* WM_COMMAND */, 1, 0);
        }, null, 0, 100);
    }

    internal bool Detected => Volatile.Read(ref warning) != null;
    internal void ThrowIfShown()
    {
        if (Volatile.Read(ref warning) is { } message) throw new Exception(message);
    }

    public void Dispose() => timer.Dispose();

    private static string ReadText(nint hwnd)
    {
        var text = new StringBuilder(4096);
        GetWindowTextW(hwnd, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string windowClass, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
