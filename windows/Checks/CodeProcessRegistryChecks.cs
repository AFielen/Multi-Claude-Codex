using System.Diagnostics;
using MultiClaude;

internal static class CodeProcessRegistryChecks
{
    internal static void Run(string home)
    {
        var catalog = new Catalog(home);
        var alpha = catalog.Save(catalog.Draft() with { Name = "Code Alpha", Shortcut = "" });
        var beta = catalog.Save(catalog.Draft() with { Name = "Code Beta", Shortcut = "" });
        var manager = new WindowsIntegration(home);
        var fixture = BuildFixture(home);
        string gate = @"Local\MultiClaude-CodeFixture-" + Guid.NewGuid().ToString("N");
        using var finish = new EventWaitHandle(false, EventResetMode.ManualReset, gate);
        ProcessStartInfo Start()
        {
            var start = new ProcessStartInfo(fixture) { UseShellExecute = false };
            start.ArgumentList.Add(gate);
            return start;
        }

        var terminal = Task.Run(() => manager.RunTerminal(beta, Start()));
        try
        {
            Parallel.Invoke(() => manager.OpenCode(alpha, Start()), () => manager.OpenCode(alpha, Start()));
            var profiles = new Catalog(home).Profiles();
            Check(SpinWait.SpinUntil(() => CodeProcessRegistry.Active(home, profiles).ContainsKey(beta.CodeKey),
                TimeSpan.FromSeconds(5)), "Attached terminal registered under Beta");
            var active = CodeProcessRegistry.Active(home, profiles);
            var consoles = active[alpha.CodeKey];
            var attached = active[beta.CodeKey].Single();
            Check(consoles.Count == 2 && consoles.Distinct().Count() == 2 && !consoles.Contains(attached),
                "Parallel consoles survive catalog reopen with separate profile attribution");
            foreach (var root in consoles.Append(attached))
            {
                using var child = Process.GetProcessById(root.Pid);
                Check(!child.HasExited && child.StartTime.ToUniversalTime().Ticks == root.StartedUtcTicks,
                    "Registration matches actual process birth");
            }
            CodeProcessRegistry.Record(home, alpha, consoles[0]);
            Check(CodeProcessRegistry.Active(home, [alpha])[alpha.CodeKey].Count == 2,
                "Duplicate registration is idempotent");
            Check(!CodeProcessRegistry.Active(home, [alpha with { Isolated = false }, beta]).ContainsKey(alpha.CodeKey),
                "Shared profiles have no attributed Code roots");
            var unreadable = CodeProcessRegistry.Active(home, [alpha with { Isolated = false }, beta], _ => null);
            Check(unreadable.Count == 1 && unreadable.TryGetValue(beta.CodeKey, out var pending)
                && pending.SequenceEqual([attached]), "An unreadable registered terminal remains a candidate only for its isolated profile");

            using (var db = new Sqlite(Path.Combine(home, "perfis.db")))
                db.Run("UPDATE code_processes SET started_utc_ticks=started_utc_ticks-1 WHERE pid=?", consoles[0].Pid);
            Check(CodeProcessRegistry.Active(home, profiles)[alpha.CodeKey].SequenceEqual([consoles[1]]),
                "A recycled PID is excluded while the other console remains");
        }
        finally { finish.Set(); }
        Check(terminal.GetAwaiter().GetResult() == 31, "Attached terminal forwards fixture exit code");
        Check(SpinWait.SpinUntil(() => CodeProcessRegistry.Active(home, [alpha, beta]).Count == 0, TimeSpan.FromSeconds(5)),
            "Exited consoles and terminal are removed");
        Check(manager.RunTerminal(null, Start()) == 31, "Shared terminal runs");
        manager.OpenCode(alpha with { Isolated = false }, Start());
        using (var db = new Sqlite(Path.Combine(home, "perfis.db")))
            Check(db.Scalar("SELECT count(*) FROM code_processes") == "0", "Shared launches write no registrations");
        var missing = new WindowsIntegration(Path.Combine(home, "missing-manager-home"));
        Check(missing.RunTerminal(beta, Start()) == 31, "Unavailable monitoring storage preserves CLI exit status");
    }

    private static string BuildFixture(string home)
    {
        string source = Path.Combine(home, "CodeFixture.cs"), exe = Path.Combine(home, "CodeFixture.exe");
        File.WriteAllText(source, """
            using System.Threading;
            class Fixture {
                static int Main(string[] args) {
                    using (var stop = EventWaitHandle.OpenExisting(args[0]))
                        return stop.WaitOne(30000) ? 31 : 72;
                }
            }
            """);
        string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        var build = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/nologo", "/target:winexe", "/out:" + exe, source }) build.ArgumentList.Add(arg);
        using var process = Process.Start(build)!;
        Check(process.WaitForExit(20000) && process.ExitCode == 0, "Build native launch fixture");
        return exe;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
