using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace MultiClaude;

internal static class CodexLauncher
{
    private static readonly object PackageLock = new();
    private static string? packageRoot;
    private static long lastPackageCheck = -60_000;

    internal static string FindDesktop() => RegisteredProgram(desktop: true)
        ?? throw new FileNotFoundException("Codex Desktop wurde nicht gefunden.");

    internal static string FindCode()
    {
        if (RegisteredProgram(desktop: false) is { } bundled) return bundled;
        foreach (string path in CliCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                string file = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
                StoragePaths.RequireOrdinary(file);
                // Older desktop installations can also put a GUI program named Codex.exe on PATH.
                using var stream = File.OpenRead(file);
                using var image = new PEReader(stream);
                if (image.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsCui &&
                    CodeLauncher.SignedBy(file, "OpenAI OpCo, LLC")) return file;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException) { }
        }
        throw new FileNotFoundException("Codex CLI wurde nicht gefunden. https://developers.openai.com/codex/cli/");
    }

    private static IEnumerable<string> CliCandidates()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string target = arch == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Prepend(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (string entry in directories)
        {
            string directory = StoragePaths.Resolve(entry.Trim().Trim('"'));
            if (directory == "") continue;
            yield return Path.Combine(directory, "codex.exe");
            string scope = Path.Combine(directory, "node_modules", "@openai");
            foreach (string vendor in new[] { Path.Combine(scope, "codex-win32-" + arch, "vendor"),
                         Path.Combine(scope, "codex", "node_modules", "@openai", "codex-win32-" + arch, "vendor"),
                         Path.Combine(scope, "codex", "vendor") })
                yield return Path.Combine(vendor, target, "codex", "codex.exe");
        }
    }

    private static string? RegisteredProgram(bool desktop)
    {
        lock (PackageLock)
        {
            if (Environment.TickCount64 - lastPackageCheck >= 60_000 || packageRoot != null && !Directory.Exists(packageRoot))
            {
                lastPackageCheck = Environment.TickCount64;
                var request = CodeLauncher.PowerShell("""
                    (Appx\Get-AppxPackage -Name 'OpenAI.Codex' |
                        Where-Object PackageFamilyName -eq 'OpenAI.Codex_2p2nqsd0c76g0' |
                        Sort-Object Version -Descending | Select-Object -First 1).InstallLocation
                    """);
                packageRoot = CodeLauncher.RunQuiet(request, out string install) ? StoragePaths.Resolve(install) : null;
                if (packageRoot == "") packageRoot = null;
            }
            return packageRoot == null ? null : PackageProgram(packageRoot, desktop);
        }
    }

    // The registered package family authenticates the installation. Read its primary desktop
    // application from the manifest because the packaged executable was renamed to ChatGPT.exe.
    internal static string? PackageProgram(string installDirectory, bool desktop)
    {
        try
        {
            string file;
            if (desktop)
            {
                var manifest = XDocument.Load(Path.Combine(installDirectory, "AppxManifest.xml"));
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                var app = manifest.Root?.Element(ns + "Applications")?.Elements(ns + "Application")
                    .FirstOrDefault(element => (string?)element.Attribute("Id") == "App");
                string? relative = ((string?)app?.Attribute("Executable"))?.Replace('/', '\\');
                if (!StoragePaths.Comparer.Equals(relative, @"app\ChatGPT.exe") &&
                    !StoragePaths.Comparer.Equals(relative, @"app\Codex.exe")) return null;
                file = Path.Combine(installDirectory, relative!);
            }
            else file = Path.Combine(installDirectory, "app", "resources", "codex.exe");
            return File.Exists(file) ? file : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException) { return null; }
    }

    internal static void Prepare(ProcessStartInfo start, string codeDirectory, string desktopDirectory)
    {
        StoragePaths.RequireOrdinary(codeDirectory);
        StoragePaths.RequireOrdinary(desktopDirectory);
        foreach (string file in new[] { "auth.json", "auth.lock", "config.toml" })
            StoragePaths.RequireUnsharedFile(Path.Combine(codeDirectory, file));
        StoragePaths.RequireOrdinary(Path.Combine(codeDirectory, "sqlite"));
        Directory.CreateDirectory(codeDirectory);

        // A manager opened from another Codex session inherits that account's IPC endpoints,
        // runtime switches and authentication overrides. Keep machine-wide TLS configuration.
        foreach (string name in start.Environment.Keys.Where(IsSessionOverride).ToList())
            start.Environment.Remove(name);
        start.Environment["CODEX_HOME"] = codeDirectory;
        start.Environment["CODEX_ELECTRON_USER_DATA_PATH"] = desktopDirectory;
    }

    private static bool IsSessionOverride(string name)
    {
        string upper = name.ToUpperInvariant();
        if (upper is "CODEX_CA_CERTIFICATE" or "CODEX_PROXY_CERT") return false;
        return upper.StartsWith("CODEX_", StringComparison.Ordinal) || upper.StartsWith("OPENAI_", StringComparison.Ordinal) ||
            upper.StartsWith("AZURE_OPENAI_", StringComparison.Ordinal) || upper.StartsWith("CHATGPT_", StringComparison.Ordinal) ||
            upper is "ELECTRON_RUN_AS_NODE" or "CDP_BROWSER_BACKEND_PIPE_PATH";
    }
}
