using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiClaude;

internal static class CodeLauncher
{
    private static readonly Regex IdentityEnvironment = new(
        @"\A(?:ANTHROPIC_(?:API_KEY|AUTH_TOKEN|BASE_URL|CUSTOM_HEADERS|PROFILE|CONFIG_DIR|ORGANIZATION_ID|(?:AWS|BEDROCK|FOUNDRY|VERTEX|GOOGLE_CLOUD|FEDERATION|IDENTITY)_.*)|CLAUDE_CODE_(?:OAUTH_.*|API_KEY_FILE_DESCRIPTOR|MESSAGING_TOKEN|USE_(?:ANTHROPIC_AWS|ANTHROPIC_GOOGLE_CLOUD|BEDROCK|FOUNDRY|MANTLE|VERTEX))|CLAUDE_CONFIG_DIR|CLAUDECODE)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string Shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private static readonly object DesktopLock = new();
    private static string? desktop;
    private static string? desktopLocal;
    private static long lastDesktopCheck = -60_000;

    internal static ProcessStartInfo StartInfo(string program) => new() { FileName = program, UseShellExecute = false };

    private static string Folder(string variable, Environment.SpecialFolder fallback)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return value != null && Regex.IsMatch(value, @"\A[A-Za-z]:[\\/]") ? value : Environment.GetFolderPath(fallback);
    }

    internal static string UserFolder => Folder("USERPROFILE", Environment.SpecialFolder.UserProfile);

    internal static void Prepare(string config)
    {
        StoragePaths.RequireOrdinary(config);
        Directory.CreateDirectory(config);
        var anthropic = Path.Combine(config, "anthropic");
        StoragePaths.RequireOrdinary(anthropic);
        Directory.CreateDirectory(anthropic);
        foreach (string name in new[] { ".credentials.json", ".claude.json", "settings.json", @"anthropic\active_config" })
            StoragePaths.RequireUnsharedFile(Path.Combine(config, name));
        StoragePaths.RequireOrdinary(Path.Combine(anthropic, "configs"));
    }

    internal static void Isolate(ProcessStartInfo child, string config)
    {
        Prepare(config);
        foreach (var name in child.Environment.Keys.Where(name => IdentityEnvironment.IsMatch(name)).ToList()) child.Environment.Remove(name);
        child.Environment["CLAUDE_CONFIG_DIR"] = config;
        child.Environment["ANTHROPIC_CONFIG_DIR"] = Path.Combine(config, "anthropic");
    }

    private static ProcessStartInfo PowerShell(string code)
    {
        var task = StartInfo(Shell);
        task.CreateNoWindow = true;
        task.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(Shell)!, "Modules");
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", code }) task.ArgumentList.Add(arg);
        return task;
    }

    private static bool RunQuiet(ProcessStartInfo request, out string output)
    {
        request.RedirectStandardOutput = true;
        request.RedirectStandardError = true;
        using var p = Process.Start(request) ?? throw new IOException("Windows PowerShell ist nicht verfügbar.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(12000))
        {
            p.Kill();
            p.WaitForExit();
            output = "";
            return false;
        }
        output = stdout.GetAwaiter().GetResult().Trim();
        _ = stderr.GetAwaiter().GetResult();
        return p.ExitCode == 0;
    }

    internal static string FindDesktop()
    {
        lock (DesktopLock)
        {
            string local = Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
            if (!string.Equals(local, desktopLocal, StringComparison.OrdinalIgnoreCase))
            {
                desktopLocal = local;
                desktop = null;
                lastDesktopCheck = -60_000;
            }
            if (desktop != null && File.Exists(desktop)) return desktop;
            if (Environment.TickCount64 - lastDesktopCheck >= 60_000)
            {
                lastDesktopCheck = Environment.TickCount64;
                var alias = Path.Combine(local, "Microsoft", "WindowsApps", "claude-desktop.exe");
                var classic = Path.Combine(local, "AnthropicClaude", "claude.exe");
                if (File.Exists(alias)) desktop = alias;
                else if (File.Exists(classic)) desktop = classic;
                else if (RunQuiet(PowerShell("(Get-AppxPackage -Name Claude | Select-Object -First 1).InstallLocation"), out var install)
                    && Path.IsPathFullyQualified(install) && File.Exists(Path.Combine(install, "app", "claude.exe")))
                    desktop = Path.Combine(install, "app", "claude.exe");
                if (desktop != null && File.Exists(desktop)) return desktop;
            }
        }
        throw new FileNotFoundException("Claude Desktop wurde nicht gefunden.");
    }

    private static IEnumerable<(string File, string Root)> CodeCandidates()
    {
        string native = Path.Combine(UserFolder, ".local");
        yield return (Path.Combine(native, "bin", "claude.exe"), native);
        string[] packagesRoots =
        [
            Path.Combine(Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages")
        ];
        foreach (string root in packagesRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var package in Directory.GetDirectories(root, "Anthropic.ClaudeCode_*"))
                yield return (Path.Combine(package, "claude.exe"), package);
        }
        var prefixes = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(x => x.Trim('"'))
            .Prepend(Path.Combine(Folder("APPDATA", Environment.SpecialFolder.ApplicationData), "npm"));
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        foreach (string prefix in prefixes.Where(x => Regex.IsMatch(x, @"\A[A-Za-z]:[\\/]")))
        {
            string scope = Path.Combine(prefix, "node_modules", "@anthropic-ai");
            yield return (Path.Combine(scope, "claude-code", "bin", "claude.exe"), scope);
            foreach (string outer in new[] { scope, Path.Combine(scope, "claude-code", "node_modules", "@anthropic-ai") })
            {
                string package = Path.Combine(outer, "claude-code-win32-" + arch);
                yield return (Path.Combine(package, "claude.exe"), package);
            }
        }
    }

    internal static string FindCode()
    {
        foreach (var (file, root) in CodeCandidates().Distinct())
        {
            try
            {
                if (!File.Exists(file)) continue;
                var resolved = new FileInfo(file).ResolveLinkTarget(true)?.FullName ?? file;
                if (!Path.GetFullPath(resolved).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                StoragePaths.RequireOrdinary(resolved);
                var info = FileVersionInfo.GetVersionInfo(resolved);
                if (info.ProductName is "Claude" or "Claude Desktop" || info.FileDescription == "Claude Desktop") continue;
                var request = PowerShell("""
                    $s=Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $env:MULTI_CLAUDE_SIGNATURE_PATH
                    if ($s.Status -eq 'Valid' -and $s.SignerCertificate.GetNameInfo('SimpleName',$false) -eq 'Anthropic, PBC') { exit 0 }
                    exit 1
                    """);
                request.Environment["MULTI_CLAUDE_SIGNATURE_PATH"] = resolved;
                if (RunQuiet(request, out _)) return resolved;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        throw new FileNotFoundException("Die signierte Windows-Version von Claude Code wurde nicht gefunden. https://code.claude.com/docs/en/setup");
    }

    internal static string QuoteArg(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var builder = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') builder.Append('\\', slashes * 2 + 1);
            else builder.Append('\\', slashes);
            builder.Append(c);
            slashes = 0;
        }
        return builder.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string PsLiteral(string value)
    {
        string[] pieces = Regex.Split(value, "(['‘’‚‛])");
        if (pieces.Length == 1) return "'" + value + "'";
        return "(" + string.Join("+", pieces.Select((s, i) => i % 2 == 0 ? "'" + s + "'" : "[char]" + (int)s[0])) + ")";
    }

    internal static void Attach(uint? pid)
    {
        if (!AttachConsole(pid ?? uint.MaxValue) && Marshal.GetLastWin32Error() != 5)
            throw new IOException("Diesen Befehl in einer PowerShell-Konsole ausführen.");
    }

    internal static void NewConsole(ProcessStartInfo start)
    {
        var args = new StringBuilder(string.Join(" ", start.ArgumentList.Prepend(start.FileName).Select(QuoteArg)));
        string environment = string.Concat(start.Environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value + "\0")) + "\0";
        var data = Marshal.StringToHGlobalUni(environment);
        try
        {
            var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessW(start.FileName, args, 0, 0, false, 0x410, data, start.WorkingDirectory, ref si, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public nint ReservedBytes, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string workingDirectory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
}
