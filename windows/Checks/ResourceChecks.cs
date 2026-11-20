using System.Diagnostics;
using MultiClaude;

internal static class ResourceChecks
{
    internal static void Run()
    {
        IncompleteSamplingNeedsANewBaseline();
        InaccessibleRootsAreBoundaries();
        TreeOwnershipAndCpu();
        NewWorkBetweenSamples();
        NativeCounters();
    }

    private static void TreeOwnershipAndCpu()
    {
        // Desktop and two CLI roots for A, plus B nested below A. PID 42 has an obsolete parent link.
        Dictionary<string, List<ProcessRoot>> roots = new()
        {
            ["A"] = [new(10, 0, 15), new(11, 11), new(30, 30)], ["B"] = [new(20, 20)],
            ["closed"] = [new(80, 8)], ["stale"] = [new(40, 1)], ["unverified"] = [new(50, 0)],
            ["reused desktop PID"] = [new(60, 0, 15)], ["unrelated executable"] = [new(70, 0, 200)]
        };
        Dictionary<int, ProcessResources.Node> tree = new()
        {
            [10] = new(1, true), [11] = new(10, false), [12] = new(11, false), [20] = new(11, false),
            [21] = new(20, false), [30] = new(1, false), [40] = new(1, false), [41] = new(40, false),
            [42] = new(10, false), [50] = new(1, true), [60] = new(1, true), [70] = new(1, false), [90] = new(1, false)
        };
        var stats = tree.Keys.ToDictionary(pid => pid, pid => new ProcessResources.Counters(pid, 0, pid));
        stats[42] = new(5, 0, 42);
        var meter = new ProcessResources();
        int seconds = 0;
        var read = new HashSet<int>();
        Dictionary<string, ResourceReading> Pass()
        {
            read.Clear();
            seconds += 2;
            return meter.SampleSnapshot(roots, tree, 1_000, TimeSpan.FromSeconds(seconds), 4, pid =>
            {
                if (!read.Add(pid)) throw new Exception("Process counters queried twice for one snapshot.");
                return stats[pid];
            });
        }

        var initial = Pass();
        if (initial.Count != 2 || initial["A"] != new ResourceReading(null, 63) || initial["B"] != new ResourceReading(null, 41))
            throw new Exception("First resource sample attributed the wrong processes.");
        if (read.Overlaps([41, 50, 70, 90])) throw new Exception("Sampler queried unrelated process counters.");
        stats[12] = new(12, TimeSpan.TicksPerSecond, 12);
        stats[30] = new(30, TimeSpan.TicksPerSecond, 30);
        var next = Pass();
        if (next["A"] != new ResourceReading(25, 63) || next["B"] != new ResourceReading(0, 41))
            throw new Exception("Per-profile CPU must sum process deltas across all logical processors.");

        // Reusing a worker PID must never import its previous process's CPU. Missing RAM stays unknown.
        stats[12] = new(60, 100 * TimeSpan.TicksPerSecond, null);
        if (Pass()["A"] != new ResourceReading(0, null)) throw new Exception("PID reuse or missing memory changed the CPU reading.");
        roots.Clear();
        if (meter.Sample(roots).Count != 0) throw new Exception("Closed profiles retained a reading.");
        roots["A"] = [new(10, 10)];
        if (Pass()["A"].CpuPercent != null) throw new Exception("A reopened profile inherited an old CPU baseline.");
    }

    private static void IncompleteSamplingNeedsANewBaseline()
    {
        Dictionary<string, List<ProcessRoot>> roots = new() { ["test"] = [new(10, 10)] };
        Dictionary<int, ProcessResources.Node> tree = new() { [10] = new(1, false), [20] = new(10, false) };
        var meter = new ProcessResources();
        ResourceReading At(int second, bool readable) => meter.SampleSnapshot(roots, tree,
            second * TimeSpan.TicksPerSecond, TimeSpan.FromSeconds(second), 4, pid =>
                pid == 10 ? new(10, 0, 40) : readable ? new(20, second * TimeSpan.TicksPerSecond, 10) : null)["test"];

        _ = At(2, true);
        if (At(4, false) != new ResourceReading(null, null)) throw new Exception("An incomplete reading was reported as complete.");
        if (At(6, true) != new ResourceReading(null, 50)) throw new Exception("An inaccessible child left a partial CPU baseline.");
        if (At(8, true) != new ResourceReading(25, 50)) throw new Exception("CPU did not recover after two complete readings.");
    }

    private static void InaccessibleRootsAreBoundaries()
    {
        Dictionary<string, List<ProcessRoot>> roots = new()
        {
            ["outer"] = [new(10, 10)], ["inner"] = [new(20, 20), new(30, 30)], ["uncertain"] = [new(20, 20)]
        };
        Dictionary<int, ProcessResources.Node> tree = new()
        {
            [10] = new(1, false), [20] = new(10, false), [21] = new(20, false), [30] = new(1, false)
        };
        var meter = new ProcessResources();
        var readings = meter.SampleSnapshot(roots, tree, 1_000, TimeSpan.FromSeconds(2), 4, pid => pid switch
        {
            20 => null,
            21 => throw new Exception("Entered the subtree of an inaccessible claimed process."),
            _ => new(pid, 0, pid)
        });
        if (readings["outer"] != new ResourceReading(null, 10) || readings["inner"] != new ResourceReading(null, null) ||
            readings["uncertain"] != new ResourceReading(null, null))
            throw new Exception("An inaccessible root distorted totals or was reported as closed.");
    }

    private static void NewWorkBetweenSamples()
    {
        Dictionary<string, List<ProcessRoot>> roots = new() { ["test"] = [new(10, 10)] };
        Dictionary<int, ProcessResources.Node> tree = new() { [10] = new(1, false) };
        Dictionary<int, ProcessResources.Counters> stats = new() { [10] = new(10, 0, 20) };
        var meter = new ProcessResources();
        ResourceReading At(int second) => meter.SampleSnapshot(roots, tree, second * TimeSpan.TicksPerSecond,
            TimeSpan.FromSeconds(second), 4, pid => stats[pid])["test"];

        _ = At(2);
        tree[20] = new(10, false);
        stats[20] = new(3 * TimeSpan.TicksPerSecond, TimeSpan.TicksPerSecond, 10);
        if (At(4) != new ResourceReading(12.5, 30)) throw new Exception("CPU of a worker born during the interval was lost.");
        // A complete restart starts a fresh baseline even when the replacement root is already busy.
        tree.Clear();
        tree[30] = new(1, false);
        stats[30] = new(5 * TimeSpan.TicksPerSecond, TimeSpan.TicksPerSecond, 40);
        roots["test"] = [new(30, 5 * TimeSpan.TicksPerSecond)];
        if (At(6) != new ResourceReading(null, 40)) throw new Exception("New profile lifetime did not warm up.");
    }

    private static void NativeCounters()
    {
        using var process = Process.GetCurrentProcess();
        long started = process.StartTime.ToUniversalTime().Ticks;
        var meter = new ProcessResources();
        Dictionary<string, List<ProcessRoot>> roots = new() { ["test"] = [new(process.Id, started)] };
        var first = meter.Sample(roots);
        if (first["test"].RamBytes is not > 0 || first["test"].CpuPercent.HasValue)
            throw new Exception("Unable to read the native working set of the current process.");
        Thread.Sleep(40);
        if (meter.Sample(roots)["test"].CpuPercent is not (>= 0 and <= 100))
            throw new Exception("Native CPU reading missing or out of range.");
        roots["test"] = [new(process.Id, started - 1)];
        if (meter.Sample(roots).ContainsKey("test")) throw new Exception("A mismatched native launch identity was accepted.");
    }
}
