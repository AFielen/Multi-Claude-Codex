using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MultiClaude;

// Code launches carry an exact start time. Desktop PIDs carry the cutoff from their WMI discovery pass.
internal readonly record struct ProcessRoot(int Pid, long StartedUtcTicks, long ObservedByUtcTicks = 0);
internal readonly record struct ResourceReading(double? CpuPercent, long? RamBytes);

internal sealed class ProcessResources
{
    internal readonly record struct Node(int ParentPid, bool IsDesktopExecutable);
    internal readonly record struct Counters(long StartedUtcTicks, long CpuTicks, long? RamBytes);

    private Dictionary<string, Dictionary<ProcessRoot, long>> previous = [];
    private TimeSpan? previousTime;
    private long previousCutoff;

    internal Dictionary<string, ResourceReading> Sample(IReadOnlyDictionary<string, List<ProcessRoot>> roots)
    {
        if (!roots.Values.Any(list => list.Count > 0))
        {
            previous.Clear();
            previousTime = null;
            return [];
        }
        long cutoff = DateTime.UtcNow.Ticks;
        var tree = Snapshot();
        uint processors = GetActiveProcessorCount(0xffff); // ALL_PROCESSOR_GROUPS
        return SampleSnapshot(roots, tree, cutoff, Stopwatch.GetElapsedTime(0),
            processors == 0 ? Environment.ProcessorCount : (int)processors, ReadCounters);
    }

    internal Dictionary<string, ResourceReading> SampleSnapshot(IReadOnlyDictionary<string, List<ProcessRoot>> roots,
        IReadOnlyDictionary<int, Node> tree, long cutoff, TimeSpan time, int processors, Func<int, Counters?> read)
    {
        var cache = new Dictionary<int, Counters?>();
        Counters? Get(int pid) => cache.TryGetValue(pid, out var value) ? value : cache[pid] = read(pid);

        // A nested launch belongs to its own profile. An ambiguously claimed root is a boundary for both.
        var owners = new Dictionary<int, string?>();
        var uncertain = new HashSet<string>();
        foreach (var (profile, candidates) in roots)
        {
            foreach (var root in candidates)
            {
                if (!tree.TryGetValue(root.Pid, out var node) || root.StartedUtcTicks < 0) continue;
                if (root.StartedUtcTicks == 0 && (!node.IsDesktopExecutable || root.ObservedByUtcTicks <= 0)) continue;
                if (Get(root.Pid) is not { } counters)
                {
                    owners[root.Pid] = null;
                    uncertain.Add(profile);
                    continue;
                }
                bool matches = root.StartedUtcTicks != 0 ? counters.StartedUtcTicks == root.StartedUtcTicks
                    : counters.StartedUtcTicks <= root.ObservedByUtcTicks;
                if (!matches || counters.StartedUtcTicks > cutoff) continue;
                if (!owners.TryAdd(root.Pid, profile) && owners[root.Pid] != profile)
                {
                    if (owners[root.Pid] is string other) uncertain.Add(other);
                    uncertain.Add(profile);
                    owners[root.Pid] = null;
                }
            }
        }

        var children = tree.ToLookup(pair => pair.Value.ParentPid, pair => pair.Key);
        var groups = uncertain.ToDictionary(profile => profile, _ => new ProfileCounters { Complete = false });
        var seen = new HashSet<int>();
        foreach (var (root, profile) in owners)
        {
            if (profile == null) continue;
            if (!groups.TryGetValue(profile, out var totals)) groups[profile] = totals = new();
            var pending = new Stack<(int Pid, long ParentStarted)>();
            pending.Push((root, 0));
            while (pending.TryPop(out var item))
            {
                if (!seen.Add(item.Pid)) continue;
                if (Get(item.Pid) is not { } counters)
                {
                    // A child exited or is inaccessible. Its descendants cannot be verified this pass.
                    totals.Complete = false;
                    continue;
                }
                // A child older than its apparent parent, or newer than the snapshot, signals PID reuse.
                if (counters.StartedUtcTicks < item.ParentStarted || counters.StartedUtcTicks > cutoff) continue;
                totals.Cpu[new(item.Pid, counters.StartedUtcTicks)] = counters.CpuTicks;
                totals.Ram += counters.RamBytes;
                foreach (int child in children[item.Pid])
                    if (!owners.ContainsKey(child)) pending.Push((child, counters.StartedUtcTicks));
            }
        }

        var result = new Dictionary<string, ResourceReading>();
        var next = new Dictionary<string, Dictionary<ProcessRoot, long>>();
        long elapsed = (time - previousTime)?.Ticks ?? 0;
        foreach (var (profile, totals) in groups)
        {
            double? cpu = null;
            if (elapsed > 0 && totals.Complete && previous.TryGetValue(profile, out var baseline))
            {
                bool continuous = false;
                double delta = 0;
                foreach (var (identity, ticks) in totals.Cpu)
                {
                    if (baseline.TryGetValue(identity, out long last))
                    {
                        if (ticks < last) continue;
                        delta += ticks - last;
                        continuous = true;
                    }
                    // Workers born during this interval have accumulated all of their CPU since the last sample.
                    else if (identity.StartedUtcTicks > previousCutoff) delta += ticks;
                }
                if (continuous) cpu = Math.Clamp(100.0 * delta / elapsed / processors, 0, 100);
            }
            result[profile] = new(cpu, totals.Complete ? totals.Ram : null);
            if (totals.Complete) next[profile] = totals.Cpu;
        }
        previous = next;
        previousTime = time;
        previousCutoff = cutoff;
        return result;
    }

    private sealed class ProfileCounters
    {
        internal Dictionary<ProcessRoot, long> Cpu { get; } = [];
        internal long? Ram = 0;
        internal bool Complete = true;
    }

    private static Dictionary<int, Node> Snapshot()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var tree = new Dictionary<int, Node>();
        do
        {
            tree[(int)entry.Pid] = new((int)entry.ParentPid,
                string.Equals(entry.Executable, "claude.exe", StringComparison.OrdinalIgnoreCase));
        } while (Process32NextW(snapshot, ref entry));
        int error = Marshal.GetLastWin32Error();
        if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        return tree;
    }

    private static Counters? ReadCounters(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !GetExitCodeProcess(process, out uint code) || code != 259 /* STILL_ACTIVE */
            || !GetProcessTimes(process, out long created, out _, out long kernel, out long user)) return null;
        return new(DateTime.FromFileTimeUtc(created).Ticks, kernel + user, ResidentMemory(process));
    }

    // Updated Windows 10/11 supplies private working set in EX2. Older systems return total working set.
    private static long? ResidentMemory(SafeProcessHandle process)
    {
        var memory = new MemoryCounters { PrivateWorkingSetSize = nuint.MaxValue };
        uint size = (uint)Marshal.SizeOf<MemoryCounters>();
        if (K32GetProcessMemoryInfo(process, ref memory, size))
            return (long)(memory.Size == size && memory.PrivateWorkingSetSize != nuint.MaxValue
                ? memory.PrivateWorkingSetSize : memory.WorkingSetSize);
        uint basicSize = (uint)Marshal.OffsetOf<MemoryCounters>(nameof(MemoryCounters.PrivateUsage));
        return K32GetProcessMemoryInfo(process, ref memory, basicSize) ? (long)memory.WorkingSetSize : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Executable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSetSize, WorkingSetSize, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool,
            PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
}
