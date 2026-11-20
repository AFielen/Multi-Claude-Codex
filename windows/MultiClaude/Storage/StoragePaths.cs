using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MultiClaude;

public static class StoragePaths
{
    internal static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex Device = new(@"^(?:CON(?:IN\$|OUT\$)?|PRN|AUX|NUL|(?:COM|LPT)[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Resolve(string? path, string? relativeTo = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        bool absolute = Regex.IsMatch(path, @"^[A-Za-z]:[\\/]");
        if (!absolute && (relativeTo == null || Path.IsPathRooted(path))) return "";
        string tail = absolute ? path[3..] : path;
        foreach (var part in tail.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") continue;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Device.IsMatch(part) ||
                part.EndsWith('.') || part.EndsWith(' ')) return "";
        }
        try { return Path.TrimEndingDirectorySeparator(absolute ? Path.GetFullPath(path) : Path.GetFullPath(path, relativeTo!)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return ""; }
    }

    public static void RequireOrdinary(string absolute)
    {
        string full = Resolve(absolute);
        if (full == "") throw new IOException("A local Windows path is required.");
        var chain = new Stack<string>();
        for (string? ancestor = full; ancestor != null; ancestor = Path.GetDirectoryName(ancestor)) chain.Push(ancestor);
        while (chain.TryPop(out var item))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(item); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                (chain.Count > 0 && !attributes.HasFlag(FileAttributes.Directory)))
                throw new IOException($"Redirected or invalid profile path: {item}");
        }
    }

    public static bool RequireUnsharedFile(string absolute)
    {
        RequireOrdinary(absolute);
        if (Directory.Exists(absolute)) throw new IOException($"Expected file: {absolute}");
        if (!File.Exists(absolute)) return false;
        EntryInfo entry;
        try { entry = FileInfoFor(absolute); }
        catch (FileNotFoundException) { return false; } // e.g. a committed SQLite rollback journal
        if (entry.Links != 1) throw new IOException($"Shared profile file: {absolute}");
        return true;
    }

    internal static bool Ordinary(string absolute)
    {
        try { RequireOrdinary(absolute); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Compare registered paths through local junctions and Windows short-name aliases.
    // Inspect each component before walking into its children; never follow a non-local link.
    internal static string? PhysicalPath(string absolute, int remaining = 12)
    {
        string full = Resolve(absolute);
        if (full == "" || remaining < 0) return null;
        string root = Path.GetPathRoot(full)!;
        string[] parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        try
        {
            for (int i = 0; i < parts.Length; i++)
            {
                string parent = current;
                current = Path.Combine(parent, parts[i]);
                var entry = new DirectoryInfo(current);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    string? prefix = LongPath(parent);
                    return prefix == null ? null : Path.Combine([prefix, .. parts[i..]]);
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    string? target = entry.LinkTarget;
                    if (target == null) return null;
                    if (target.StartsWith(@"\\?\", StringComparison.Ordinal))
                    {
                        target = target[4..];
                        if (Resolve(target) == "") return null;
                    }
                    if (target.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return null;
                    string resolved = Resolve(target, Path.GetDirectoryName(current));
                    if (resolved == "") return null;
                    return PhysicalPath(Path.Combine([resolved, .. parts[(i + 1)..]]), remaining - 1);
                }
            }
            return LongPath(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string? LongPath(string path)
    {
        var buffer = new System.Text.StringBuilder(Math.Max(path.Length + 1, 260));
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length >= buffer.Capacity && length <= 32768)
        {
            buffer.EnsureCapacity((int)length);
            length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        }
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string path, System.Text.StringBuilder output, uint length);

    internal static bool Contains(string root, string path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
        return Comparer.Equals(root, path) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Slug(string name)
    {
        string slug = Regex.Replace(name, @"[^\w-]+", "_").Trim('_');
        if (slug.Length == 0) slug = "perfil";
        if (slug.Length > 80) slug = slug[..80].TrimEnd();
        return Device.IsMatch(slug) ? "_" + slug : slug;
    }

    // Direct filesystem identities prevent a retry from erasing a replacement directory.
    [StructLayout(LayoutKind.Sequential)]
    private struct EntryInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, FileHigh, FileLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out EntryInfo info);
    private static EntryInfo FileInfoFor(string path)
    {
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) throw new FileNotFoundException("File no longer exists.", path);
            throw new IOException($"Cannot inspect {path}.", new System.ComponentModel.Win32Exception(error));
        }
        return info;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdInfo info, uint size);

    internal static string Identity(string path)
    {
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        // Python 3.12+ stat() uses FILE_ID_INFO for its volume serial and 128-bit inode on NTFS/ReFS.
        if (!handle.IsInvalid && GetFileInformationByHandleEx(handle, 18, out var full, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            UInt128 index = (UInt128)full.High << 64 | full.Low;
            return FormattableString.Invariant($"{full.Volume}:{index}");
        }
        var info = FileInfoFor(path);
        ulong shortIndex = (ulong)info.FileHigh << 32 | info.FileLow;
        return FormattableString.Invariant($"{info.Volume}:{shortIndex}");
    }
}
