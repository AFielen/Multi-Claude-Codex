using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace MultiClaude;

internal static class CodeSharing
{
    private static readonly string[] Resources = ["skills", "agents", "commands", "CLAUDE.md"];
    private const string Import = "<!-- Multi Claude shared instructions -->\n@~/.claude/CLAUDE.md\n";
    private sealed record Identity(ulong Volume, BigInteger File);

    internal static void Sync(string config, string source, bool enabled, bool refresh)
    {
        StoragePaths.RequireOrdinary(config);
        var guard = Path.Combine(config, ".multi-claude-sharing.lock");
        StoragePaths.RequireUnsharedFile(guard);
        using var gate = new FileStream(guard, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        gate.Lock(0, 1);
        try
        {
            int mode = gate.ReadByte();
            if (refresh && mode == '0') return;
            Reconcile(config, source, enabled);
            int desired = enabled ? '1' : '0';
            if (mode != desired)
            {
                gate.Position = 0;
                gate.WriteByte((byte)desired);
                gate.Flush(true);
            }
        }
        finally { gate.Unlock(0, 1); }
    }

    private static void Reconcile(string config, string source, bool enabled)
    {
        var manifest = Path.Combine(config, ".multi-claude-shared.json");
        var owned = ReadManifest(manifest);
        var created = new Dictionary<string, Identity>();
        var staged = new Dictionary<string, string>();
        var changed = false;
        try
        {
            foreach (var name in Resources)
            {
                var src = Path.Combine(source, name);
                var dest = Path.Combine(config, name);
                var directory = name != "CLAUDE.md";
                if (owned.TryGetValue(name, out var previous) && previous != LinkIdentity(dest, src, directory))
                {
                    owned.Remove(name);
                    changed = true;
                }
                if (!enabled && owned.Remove(name))
                {
                    string backup = Path.Combine(config, ".sharing-" + Guid.NewGuid().ToString("N"));
                    Move(dest, backup, directory);
                    staged[name] = backup;
                    changed = true;
                }
                if (!enabled || Exists(dest)) continue;
                StoragePaths.RequireOrdinary(src);
                if (directory ? !Directory.Exists(src) : !StoragePaths.RequireUnsharedFile(src)) continue;
                if (directory) Junction(src, dest);
                else
                {
                    using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(Encoding.UTF8.GetBytes(Import));
                }
                var identity = LinkIdentity(dest, src, directory) ?? throw new IOException("Die neue Ressourcenverknüpfung konnte nicht geprüft werden.");
                created[name] = owned[name] = identity;
                changed = true;
            }
            if (changed)
            {
                var json = new JsonObject();
                foreach (var (name, id) in owned)
                    json[name] = JsonNode.Parse(FormattableString.Invariant($"[{id.Volume},{id.File}]"));
                AtomicWrite(manifest, json.ToJsonString());
            }
        }
        catch
        {
            foreach (var (name, tmp) in staged)
            {
                var target = Path.Combine(config, name);
                if (!Exists(target)) Move(tmp, target, name != "CLAUDE.md");
            }
            foreach (var (name, id) in created)
            {
                var target = Path.Combine(config, name);
                if (id == LinkIdentity(target, Path.Combine(source, name), name != "CLAUDE.md"))
                    Remove(target, name != "CLAUDE.md");
            }
            throw;
        }
        foreach (var (name, tmp) in staged) Remove(tmp, name != "CLAUDE.md");
    }

    private static Dictionary<string, Identity> ReadManifest(string path)
    {
        var result = new Dictionary<string, Identity>(StringComparer.Ordinal);
        if (!StoragePaths.RequireUnsharedFile(path)) return result;
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Ungültiges Ressourcenverzeichnis.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var prop in json.RootElement.EnumerateObject())
        {
            var array = prop.Value;
            if (!Resources.Contains(prop.Name) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 2
                || !array[0].TryGetUInt64(out var volume) || !BigInteger.TryParse(array[1].GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var file))
                throw new InvalidDataException("Ungültiges Ressourcenverzeichnis.");
            result.Add(prop.Name, new Identity(volume, file));
        }
        return result;
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void Move(string src, string dst, bool directory)
    {
        if (directory) Directory.Move(src, dst);
        else File.Move(src, dst);
    }

    private static void Remove(string path, bool directory)
    {
        if (directory) Directory.Delete(path, false);
        else File.Delete(path);
    }

    internal static void AtomicWrite(string file, string contents)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(file)!, ".multi-claude-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(contents);
            File.Move(tmp, file, overwrite: true);
        }
        finally { File.Delete(tmp); }
    }

    private static Identity? LinkIdentity(string file, string source, bool directory)
    {
        if (!Exists(file)) return null;
        if (directory)
        {
            var entry = new DirectoryInfo(file);
            if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || !DesktopWindows.SamePath(entry.LinkTarget ?? "", source)) return null;
        }
        else
        {
            if ((File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
            try { if (!StoragePaths.RequireUnsharedFile(file)) return null; }
            catch (IOException) { return null; } // Existing user-owned or inaccessible replacement.
            if (new FileInfo(file).Length != Encoding.UTF8.GetByteCount(Import)
                || !File.ReadAllBytes(file).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Import))) return null;
        }
        using var handle = OpenEntry(file, 0);
        byte[] buffer = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, buffer, (uint)buffer.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(BinaryPrimitives.ReadUInt64LittleEndian(buffer), new BigInteger(buffer.AsSpan(8), true, false));
    }

    private static SafeFileHandle OpenEntry(string path, uint access)
    {
        var file = CreateFileW(path, access, 7, 0, 3, 0x02200000, 0); // OPEN_REPARSE_POINT | BACKUP_SEMANTICS
        if (file.IsInvalid) { file.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return file;
    }

    private static void Junction(string source, string dest)
    {
        // Mount-point reparse data stores the target in NT form and its display name in DOS form.
        var target = Encoding.Unicode.GetBytes(@"\??\" + source);
        var display = Encoding.Unicode.GetBytes(source);
        byte[] payload = new byte[16 + target.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xa0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), checked((ushort)(payload.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), checked((ushort)target.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), checked((ushort)(target.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), checked((ushort)display.Length));
        target.CopyTo(payload, 16);
        display.CopyTo(payload, 16 + target.Length + 2);
        if (!CreateDirectoryW(dest, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var handle = OpenEntry(dest, 0x40000000);
            if (!DeviceIoControl(handle, 0x900a4, payload, payload.Length, 0, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Directory.Delete(dest, false); throw; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string file, uint access, uint share, nint attributes, uint type, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string directory, nint attributes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int size, nint output, int outputSize, out int actual, nint overlap);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int @class, [Out] byte[] buffer, uint size);
}
