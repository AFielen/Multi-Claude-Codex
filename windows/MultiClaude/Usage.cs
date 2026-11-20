using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MultiClaude;

public sealed record LimitWindow(double Percent, DateTimeOffset Resets);
public sealed record UsageSnapshot(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite,
    int Requests, bool Partial, bool Capture, DateTimeOffset? Observed, LimitWindow? FiveHour, LimitWindow? SevenDay)
{
    public decimal Total => Input + Output + CacheRead + CacheWrite;
}

public static class Usage
{
    private const int LineLimit = 8 * 1024 * 1024;
    private const string Flag = "multiclaude-limits.enabled";
    private const string Snapshot = "multiclaude-limits.json";
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };
    private sealed record Sample(bool Finished, long Input, long Output, long Read, long Write)
    {
        public (bool, long, long, long, long) Rank => (Finished, Output, Input, Read, Write);
    }
    private sealed record Transcript(string Identity, long Size, DateTime Modified, Dictionary<string, Sample> Samples, bool Partial);
    private static readonly Dictionary<string, Transcript> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static UsageSnapshot Read(Profile p, CancellationToken token = default)
    {
        if (!p.Isolated) return new(0, 0, 0, 0, 0, false, false, null, null, null);
        var root = Root(p);
        var limits = Limits(root);
        bool capture = CaptureEnabled(root);
        bool partial = false;
        var samples = new Dictionary<string, Sample>(StringComparer.Ordinal);
        var projects = Path.Combine(root, "projects");
        if (Directory.Exists(projects))
        {
            var dirs = new Stack<(string Dir, int Depth)>();
            dirs.Push((projects, 0));
            while (dirs.TryPop(out var entry))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    StoragePaths.RequireOrdinary(entry.Dir);
                    foreach (var file in new DirectoryInfo(entry.Dir).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (file is DirectoryInfo)
                        {
                            if (entry.Depth == 1 && file.Name == "memory" || entry.Depth == 2 && file.Name != "subagents") continue;
                            dirs.Push((file.FullName, entry.Depth + 1));
                        }
                        else if ((entry.Depth == 1 || entry.Depth >= 3) && file.Extension == ".jsonl")
                        {
                            try
                            {
                                var transcript = ReadTranscript((FileInfo)file, token);
                                partial |= transcript.Partial;
                                foreach (var (id, sample) in transcript.Samples) Merge(samples, id, sample);
                            }
                            catch (Exception e) when (FileError(e)) { partial = true; }
                        }
                    }
                }
                catch (Exception e) when (FileError(e)) { partial = true; }
            }
        }
        decimal input = 0, output = 0, read = 0, write = 0;
        foreach (var sample in samples.Values)
        {
            input += sample.Input; output += sample.Output; read += sample.Read; write += sample.Write;
            partial |= !sample.Finished;
        }
        return new(input, output, read, write, samples.Count, partial, capture, limits.Observed, limits.Five, limits.Seven);
    }

    private static void Merge(Dictionary<string, Sample> map, string id, Sample sample)
    {
        if (!map.TryGetValue(id, out var old) || sample.Rank.CompareTo(old.Rank) > 0) map[id] = sample;
    }

    private static Transcript ReadTranscript(FileInfo file, CancellationToken token)
    {
        if (!StoragePaths.RequireUnsharedFile(file.FullName)) throw new FileNotFoundException();
        string identity = StoragePaths.Identity(file.FullName);
        lock (Cache)
            if (Cache.TryGetValue(file.FullName, out var entry) && entry.Identity == identity && entry.Size == file.Length && entry.Modified == file.LastWriteTimeUtc)
                return entry;
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var rows = new Dictionary<string, Sample>(StringComparer.Ordinal);
        bool partial = false, oversized = false;
        using var line = new MemoryStream();
        byte[] block = new byte[65536];
        void EndLine()
        {
            if (oversized) partial = true;
            else
            {
                try { ParseLine(line.GetBuffer().AsMemory(0, (int)line.Length), rows); }
                catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException) { partial = true; }
            }
            line.SetLength(0);
            oversized = false;
        }
        int count;
        while ((count = stream.Read(block)) > 0)
        {
            token.ThrowIfCancellationRequested();
            int start = 0;
            while (start < count)
            {
                int end = Array.IndexOf(block, (byte)'\n', start, count - start);
                int span = (end < 0 ? count : end) - start;
                oversized |= line.Length + span > LineLimit;
                if (!oversized) line.Write(block, start, span);
                if (end < 0) break;
                EndLine();
                start = end + 1;
            }
        }
        if (line.Length > 0 || oversized) EndLine();
        var result = new Transcript(identity, file.Length, file.LastWriteTimeUtc, rows, partial);
        lock (Cache)
        {
            if (Cache.Count >= 128 && !Cache.ContainsKey(file.FullName)) Cache.Remove(Cache.Keys.First());
            Cache[file.FullName] = result;
        }
        return result;
    }

    private static void ParseLine(ReadOnlyMemory<byte> line, Dictionary<string, Sample> rows)
    {
        if (line.Span.StartsWith(Encoding.UTF8.Preamble)) line = line[Encoding.UTF8.Preamble.Length..];
        using var doc = JsonDocument.Parse(line);
        var row = doc.RootElement;
        if (!row.TryGetProperty("type", out var type) || type.GetString() != "assistant") return;
        var msg = row.GetProperty("message");
        if (msg.TryGetProperty("model", out var model) && model.GetString() == "<synthetic>" ||
            !msg.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null) return;
        string? id = msg.GetProperty("id").GetString();
        if (id is not { Length: > 0 and <= 256 }) throw new FormatException();
        bool any = false;
        long Count(string key)
        {
            if (!usage.TryGetProperty(key, out var value)) return 0;
            any = true;
            if (!value.TryGetInt64(out long result) || result < 0) throw new FormatException();
            return result;
        }
        var sample = new Sample(msg.TryGetProperty("stop_reason", out var reason) && !string.IsNullOrEmpty(reason.GetString()),
            Count("input_tokens"), Count("output_tokens"), Count("cache_read_input_tokens"), Count("cache_creation_input_tokens"));
        if (!any) throw new FormatException();
        Merge(rows, id, sample);
    }

    private static string Root(Profile p)
    {
        if (!p.Isolated || !Regex.IsMatch(p.CodeKey, @"\A[a-f0-9]{32}\z")) throw new ArgumentException("Expected an isolated Claude Code profile.");
        var root = Path.Combine(p.CodeHome, "claude-code", p.CodeKey);
        StoragePaths.RequireOrdinary(root);
        return root;
    }

    private static bool FileError(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException;
    private static byte[]? ReadFile(string path, int max)
    {
        if (!StoragePaths.RequireUnsharedFile(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > max) throw new InvalidDataException("Profile metadata exceeds its size limit.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static string? Marker(string root)
    {
        var bytes = ReadFile(Path.Combine(root, Flag), 97);
        if (bytes?.Length != 97) return null;
        string s = Encoding.ASCII.GetString(bytes);
        return s.StartsWith(Path.GetFileName(root) + ":", StringComparison.Ordinal) && Regex.IsMatch(s[33..], @"\A[a-f0-9]{64}\z") ? s[33..] : null;
    }
    private static string? Digest(JsonNode? node)
    {
        if (node is not JsonObject obj || obj.Count != 2 || obj["type"]?.ToString() != "command" ||
           obj["command"] is not JsonValue value || !value.TryGetValue<string>(out var command)) return null;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command)));
    }
    private static string MetadataText(byte[] bytes)
    {
        // Windows PowerShell can write both UTF-8 with BOM and UTF-16 preferences.
        using var input = new MemoryStream(bytes, false);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static JsonObject ParseSettings(byte[] bytes) => JsonNode.Parse(MetadataText(bytes), null, StrictJson) as JsonObject ?? throw new JsonException();

    private static bool CaptureEnabled(string root)
    {
        try
        {
            string? marker = Marker(root);
            return marker != null && ReadFile(Path.Combine(root, "settings.json"), 1 << 20) is byte[] bytes && Digest(ParseSettings(bytes)["statusLine"]) == marker;
        }
        catch (Exception e) when (FileError(e) || e is JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    public static (DateTimeOffset? Observed, LimitWindow? Five, LimitWindow? Seven) ReadLimits(Profile p) => p.Isolated ? Limits(Root(p)) : default;

    private static (DateTimeOffset? Observed, LimitWindow? Five, LimitWindow? Seven) Limits(string root)
    {
        try
        {
            if (Marker(root) == null || ReadFile(Path.Combine(root, Snapshot), 4096) is not byte[] bytes) return default;
            using var doc = JsonDocument.Parse(MetadataText(bytes), StrictJson);
            var data = doc.RootElement;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!data.TryGetProperty("observed_at", out var stamp) || !stamp.TryGetInt64(out long when) || when <= 0 || when > now + 2) return default;
            LimitWindow? Window(string key)
            {
                if (!data.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object ||
                    !w.TryGetProperty("used_percentage", out var pct) || pct.ValueKind != JsonValueKind.Number || !pct.TryGetDouble(out double used) || used is < 0 or > 100 ||
                    !w.TryGetProperty("resets_at", out var reset) || reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out long end) || end <= Math.Max(now, when) || end > 253402300799) return null;
                return new(used, DateTimeOffset.FromUnixTimeSeconds(end));
            }
            var five = Window("five_hour"); var seven = Window("seven_day");
            return five == null && seven == null ? default : (DateTimeOffset.FromUnixTimeSeconds(when), five, seven);
        }
        catch (Exception e) when (FileError(e) || e is JsonException or InvalidOperationException) { return default; }
    }

    public static bool SetCapture(Profile p, bool enable)
    {
        var root = Root(p);
        string settings = Path.Combine(root, "settings.json"), flag = Path.Combine(root, Flag), target = Path.Combine(root, Snapshot);
        try
        {
            if (!enable && !Directory.Exists(root)) return true;
            Directory.CreateDirectory(root);
            string lockFile = Path.Combine(root, ".multiclaude-settings.lock");
            StoragePaths.RequireUnsharedFile(lockFile);
            using var lease = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            lease.Lock(0, 1);
            byte[]? before = ReadFile(settings, 1 << 20);
            var obj = before == null ? new JsonObject() : ParseSettings(before);
            var installed = Marker(root);
            StoragePaths.RequireUnsharedFile(target);
            if (File.Exists(flag) && installed == null) return false;
            string command = CaptureCommand(p.CodeKey);
            var own = new JsonObject { ["type"] = "command", ["command"] = command };
            string digest = Digest(own)!;
            bool hasStatus = obj.TryGetPropertyValue("statusLine", out var status);
            if (hasStatus && Digest(status) != digest && (installed == null || Digest(status) != installed))
            {
                if (!enable && installed != null) { File.Delete(flag); File.Delete(target); }
                return false;
            }
            if (enable) obj["statusLine"] = own;
            else obj.Remove("statusLine");
            if ((enable && (!hasStatus || Digest(status) != digest)) || (!enable && hasStatus))
            {
                var payload = Encoding.UTF8.GetBytes(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                string temp = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temp, payload);
                    byte[]? current = ReadFile(settings, 1 << 20);
                    if ((before == null) != (current == null) || before != null && !before.AsSpan().SequenceEqual(current)) return false;
                    File.Move(temp, settings, before != null);
                }
                finally { File.Delete(temp); }
            }
            if (enable && installed != digest)
            {
                string temp = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
                try { File.WriteAllText(temp, p.CodeKey + ":" + digest, Encoding.ASCII); File.Move(temp, flag, true); }
                finally { File.Delete(temp); }
            }
            if (!enable && installed != null) { File.Delete(flag); File.Delete(target); }
            return true;
        }
        catch (Exception e) when (FileError(e) || e is JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    internal static string CaptureCommand(string key)
    {
        using var resource = typeof(Usage).Assembly.GetManifestResourceStream("MultiClaude.Resources.Capture.ps1")!;
        using var reader = new StreamReader(resource);
        string script = reader.ReadToEnd().Replace("\r\n", "\n").Replace("__UUID__", key);
        string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe").Replace('\\', '/');
        if (!File.Exists(shell) || !Regex.IsMatch(shell, @"\A[A-Za-z]:/[A-Za-z0-9_./~-]+\z")) throw new IOException("Windows PowerShell path is unavailable.");
        return shell + " -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public static (long Bytes, bool Partial) FolderSize(Catalog catalog, Profile p, CancellationToken token = default)
    {
        catalog.ValidateDesktop(p);
        if (!Directory.Exists(p.DesktopPath)) return (0, false);
        var timer = Stopwatch.StartNew();
        long bytes = 0;
        bool partial = false;
        var dirs = new Stack<string>();
        dirs.Push(p.DesktopPath);
        while (dirs.TryPop(out var path))
        {
            try
            {
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (timer.ElapsedMilliseconds >= 1500) return (bytes, true);
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo) dirs.Push(entry.FullName);
                    else if (entry is FileInfo file) bytes += file.Length;
                }
            }
            catch (Exception e) when (FileError(e)) { partial = true; }
        }
        return (bytes, partial);
    }
}
