using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace MultiClaude;

internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr hwnd, string key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr hwnd, string key, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr hwnd, string key);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetLongPathName(string path, StringBuilder result, uint size);

    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Multi Claude · WPF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.FirstOrDefault() is "--link" or "--browser")
        {
            MessageBox.Show("Choose your browser for web links and Claude Desktop for claude:// under Windows Settings → Apps → Default apps.", "Multi Claude");
            return 0;
        }
        var command = ManagerArguments.Parse(args, Environment.GetEnvironmentVariable("MULTI_CLAUDE_HOME") ?? AppContext.BaseDirectory);
        if (command.Kind == "--code-terminal")
        {
            Profile? selected = null;
            if (command.Key != "shared")
            {
                selected = Catalog.FindInExisting(command.Home, command.Key!)
                    ?? throw new ArgumentException("The terminal profile is no longer registered.");
                if (!selected.Isolated) throw new ArgumentException("This profile now uses shared Claude Code settings.");
            }
            return new WindowsIntegration(command.Home).RunTerminal(selected, command.Login, command.ConsolePid);
        }
        if (command.Key != null && Catalog.FindInExisting(command.Home, command.Key) == null)
            throw new ArgumentException("The shortcut's profile or catalog is no longer available.");

        StoragePaths.RequireOrdinary(command.Home);
        Directory.CreateDirectory(command.Home);
        var expanded = new StringBuilder(32768);
        var identity = GetLongPathName(command.Home, expanded, (uint)expanded.Capacity) is > 0 and < 32768
            ? expanded.ToString() : command.Home;
        var name = Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(identity).ToLowerInvariant());
        string tag = "MultiClaude.Manager." + Convert.ToHexStringLower(SHA256.HashData(name));
        using var mutex = new Mutex(false, "MultiClaude_" + Convert.ToHexStringLower(MD5.HashData(name))[..16], out bool first);

        if (!first)
        {
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if ((long)GetProp(window, tag) != pid || pid == 0) return true;
                AllowSetForegroundWindow((int)pid);
                if (command.Key == null)
                {
                    if (IsIconic(window)) ShowWindowAsync(window, 9);
                    SetForegroundWindow(window);
                }
                return false;
            }, IntPtr.Zero);
            if (command.Key != null) new Catalog(command.Home).Enqueue(command.Key);
            return 0;
        }
        SetCurrentProcessExplicitAppUserModelID("RichardKPL.MultiClaude");
        var catalog = new Catalog(command.Home);
        // Old browser-handler state must never be replayed against a new selection.
        foreach (string key in new[] { "login_pasta", "login_url", "login_ok" }) catalog.Set(key, null);
        WriteLog(catalog.Home, "Multi Claude 2.3.0 WPF");
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            WriteLog(catalog.Home, e.Exception.GetType().Name);
            e.Handled = true;
            MessageBox.Show(e.Exception.Message, "Multi Claude", MessageBoxButton.OK, MessageBoxImage.Error);
            app.Shutdown(1);
        };
        Theme.Apply(catalog.Get("tema") ?? "sistema");
        var main = new MainWindow(catalog);
        IntPtr hwnd = IntPtr.Zero;
        main.SourceInitialized += (_, _) =>
        {
            hwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
            SetProp(hwnd, tag, Environment.ProcessId);
        };
        if (command.Key != null) catalog.Enqueue(command.Key);
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        poll.Tick += (_, _) =>
        {
            try { if (main.IsEnabled && main.OwnedWindows.Cast<Window>().All(w => !w.IsVisible) && catalog.Dequeue() is string key) main.ActivateProfile(key); }
            catch (Exception error) { poll.Stop(); MessageBox.Show(main, error.Message, main.Title); poll.Start(); }
        };
        main.Loaded += (_, _) => poll.Start();
        main.Closing += (_, _) => { poll.Stop(); if (hwnd != IntPtr.Zero) RemoveProp(hwnd, tag); };
        return app.Run(main);
    }

    private static void WriteLog(string home, string message)
    {
        try
        {
            var log = Path.Combine(home, "log.txt");
            if (StoragePaths.RequireUnsharedFile(log) && new FileInfo(log).Length > 500_000)
            {
                StoragePaths.RequireUnsharedFile(log + ".old");
                File.Move(log, log + ".old", true);
            }
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed record ManagerArguments(string? Kind, string? Key, string Home, bool Login, uint? ConsolePid)
{
    internal static ManagerArguments Parse(string[] args, string defaultHome)
    {
        int i = 0;
        string? kind = null, key = null;
        bool login = false;
        uint? pid = null;
        if (args.ElementAtOrDefault(i) is "--open-profile" or "--code-terminal")
        {
            kind = args[i++];
            key = args.ElementAtOrDefault(i++) ?? "";
            if (!(key == "shared" && kind == "--code-terminal") && !Regex.IsMatch(key, @"\A[a-f0-9]{32}\z"))
                throw new ArgumentException("Invalid profile key.");
        }
        if (kind == "--open-profile" && args.ElementAtOrDefault(i) != "--manager-home")
            throw new ArgumentException("A shortcut must identify its original manager folder.");
        string home = defaultHome;
        if (args.ElementAtOrDefault(i) == "--manager-home")
        {
            i++;
            home = args.ElementAtOrDefault(i++) ?? "";
        }
        if (kind == "--code-terminal" && args.ElementAtOrDefault(i) == "--login")
        {
            if (key == "shared") throw new ArgumentException("Choose an isolated profile to sign in.");
            login = true;
            i++;
        }
        if (kind == "--code-terminal" && args.ElementAtOrDefault(i) == "--console-pid")
        {
            i++;
            if (!uint.TryParse(args.ElementAtOrDefault(i++), out uint value) || value == 0)
                throw new ArgumentException("Invalid console process ID.");
            pid = value;
        }
        if (i != args.Length || !Regex.IsMatch(home, @"\A[A-Za-z]:[\\/]") ||
            home.Split('\\', '/').Contains(".."))
            throw new ArgumentException("Expected a local manager folder and valid profile arguments.");
        return new(kind, key, Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)), login, pid);
    }
}
