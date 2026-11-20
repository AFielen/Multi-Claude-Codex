using System.Text.Json;
using MultiClaude;

internal static class UsageChecks
{
    internal static void Run(string home)
    {
        var catalog = new Catalog(home);
        var p = catalog.Save(catalog.Draft() with { Name = "Quota A" });
        var q = catalog.Save(catalog.Draft() with { Name = "Quota B" });
        var dir = Path.Combine(p.CodeHome, "claude-code", p.CodeKey);
        var project = Directory.CreateDirectory(Path.Combine(dir, "projects", "synthetic"));
        var transcripts = Path.Combine(project.FullName, "session.jsonl");
        string Sample(int output, bool final) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { id = "msg-demo", usage = new { input_tokens = 12, output_tokens = output }, stop_reason = final ? "end_turn" : null }
        });
        File.WriteAllLines(transcripts, [Sample(3, false), Sample(9, true), Sample(11, false), "bad record"]);
        var u = Usage.Read(p);
        Assert(u.Total == 21 && u.Requests == 1 && u.Partial, "streaming token deduplication and partial reporting");
        Assert(Usage.Read(q).Total == 0, "profile boundary");
        string settings = Path.Combine(dir, "settings.json");
        File.WriteAllText(settings, "{\"keep\":123,\"statusLine\":{\"type\":\"command\",\"command\":\"user-command\"}}");
        Assert(!Usage.SetCapture(p, true) && File.ReadAllText(settings).Contains("user-command"), "foreign statusLine survives");
        File.WriteAllText(settings, "{\"keep\":123}", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert(Usage.SetCapture(p, true) && Usage.Read(p).Capture, "capture enabled");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string snapshot = Path.Combine(dir, "multiclaude-limits.json");
        File.WriteAllText(snapshot, JsonSerializer.Serialize(new { observed_at = now - 4, five_hour = new { used_percentage = 82.5, resets_at = now + 200 }, seven_day = new { used_percentage = 3, resets_at = now - 1 } }));
        u = Usage.Read(p);
        Assert(u.FiveHour?.Percent == 82.5 && u.SevenDay is null && u.Observed?.ToUnixTimeSeconds() == now - 4, "active limits and reset filtering");
        var command = JsonDocument.Parse(File.ReadAllBytes(settings)).RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
        const string transport = " -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
        var split = command.Split(transport, StringSplitOptions.None);
        Assert(split.Length == 2 && File.Exists(split[0]), "installed status command");
        var start = new System.Diagnostics.ProcessStartInfo(split[0]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", split[1] }) start.ArgumentList.Add(arg);
        start.Environment["CLAUDE_CONFIG_DIR"] = dir;
        using var collector = System.Diagnostics.Process.Start(start)!;
        collector.StandardInput.WriteLine(JsonSerializer.Serialize(new { rate_limits = new { five_hour = new { used_percentage = 86, resets_at = now + 200 } } }));
        collector.StandardInput.Close();
        var report = collector.StandardOutput.ReadToEndAsync();
        var diagnostics = collector.StandardError.ReadToEndAsync();
        if (!collector.WaitForExit(10000))
        {
            collector.Kill();
            throw new TimeoutException("Status line did not finish.");
        }
        Assert(collector.ExitCode == 0 && report.GetAwaiter().GetResult().Contains("86%") && Usage.Read(p).FiveHour?.Percent == 86,
            "Claude Code statusLine emits current profile limit: " + diagnostics.GetAwaiter().GetResult());
        File.WriteAllText(snapshot, new string(' ', 4097));
        Assert(Usage.Read(p) is { Total: 21, FiveHour: null }, "oversized limit data does not hide token totals");
        Assert(Usage.SetCapture(p, false) && !Usage.Read(p).Capture && !File.Exists(snapshot), "capture disabled");
        Assert(JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("keep").GetInt32() == 123, "preserve other CLI settings");
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
