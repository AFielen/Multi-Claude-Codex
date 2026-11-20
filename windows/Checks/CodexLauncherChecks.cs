using System.Diagnostics;
using MultiClaude;

internal static class CodexLauncherChecks
{
    public static void Run(string tempRoot)
    {
        string package = Directory.CreateDirectory(Path.Combine(tempRoot, "Codex package")).FullName;
        string app = Directory.CreateDirectory(Path.Combine(package, "app")).FullName;
        string cli = Path.Combine(Directory.CreateDirectory(Path.Combine(app, "resources")).FullName, "codex.exe");
        File.WriteAllText(cli, "");
        foreach (string name in new[] { "Codex.exe", "ChatGPT.exe" })
        {
            string desktop = Path.Combine(app, name);
            File.WriteAllText(desktop, "");
            File.WriteAllText(Path.Combine(package, "AppxManifest.xml"), $"""
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Applications>
                    <Application Id="Helper" Executable="app/resources/codex.exe"/>
                    <Application Id="App" Executable="app/{name}"/>
                  </Applications>
                </Package>
                """);
            if (CodexLauncher.PackageProgram(package, desktop: true) != desktop ||
                CodexLauncher.PackageProgram(package, desktop: false) != cli)
                throw new Exception("Codex desktop and CLI must follow distinct package entry points.");
        }

        string first = Path.Combine(tempRoot, "Codex account A");
        string second = Path.Combine(tempRoot, "Codex account B");
        var child = new ProcessStartInfo();
        string[] overrides = ["OPENAI_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL", "OPENAI_IDENTITY_TOKEN_FILE",
            "CODEX_SQLITE_HOME", "CODEX_APP_SERVER_CHATGPT_BASE_URL", "CODEX_APP_TOOLS_PIPE_PATH", "CODEX_CLI_PATH",
            "CODEX_THREAD_ID", "ELECTRON_RUN_AS_NODE"];
        foreach (string key in overrides) child.Environment[key] = "fixture";
        child.Environment["CODEX_CA_CERTIFICATE"] = "fixture-certificate";
        foreach (string account in new[] { first, second })
        {
            string code = Path.Combine(account, "code"), desktop = Path.Combine(account, "desktop");
            CodexLauncher.Prepare(child, code, desktop);
            if (child.Environment["CODEX_HOME"] != code || child.Environment["CODEX_ELECTRON_USER_DATA_PATH"] != desktop ||
                !Directory.Exists(code) || overrides.Any(child.Environment.ContainsKey) ||
                child.Environment["CODEX_CA_CERTIFICATE"] != "fixture-certificate")
                throw new Exception("Codex launcher did not isolate the selected profile.");
        }
    }
}
