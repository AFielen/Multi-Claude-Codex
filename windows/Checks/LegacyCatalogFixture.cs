using Microsoft.Win32.SafeHandles;
using MultiClaude;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;

internal static class LegacyCatalogFixture
{
    // Keep the on-disk 2.3.0 schema independent of the current Catalog constructor.
    internal static void CreateSchema(Sqlite db)
    {
        db.Run("""
            CREATE TABLE perfis (
                id INTEGER PRIMARY KEY, nome TEXT UNIQUE NOT NULL, pasta TEXT NOT NULL,
                criado TEXT DEFAULT CURRENT_TIMESTAMP, foto TEXT)
            """);
        db.Run("CREATE TABLE config (chave TEXT PRIMARY KEY, valor TEXT)");
        db.Run("""
            CREATE TABLE profile_options (
                profile INTEGER PRIMARY KEY REFERENCES perfis(id) ON DELETE CASCADE,
                code_key TEXT UNIQUE NOT NULL, position INTEGER NOT NULL, color TEXT NOT NULL,
                shortcut TEXT NOT NULL, isolated INTEGER NOT NULL, border INTEGER NOT NULL DEFAULT 0,
                sharing INTEGER NOT NULL DEFAULT 0, last_open TEXT, tray INTEGER NOT NULL DEFAULT 0)
            """);
        db.Run("""
            CREATE TRIGGER remove_profile_options AFTER DELETE ON perfis
            BEGIN DELETE FROM profile_options WHERE profile=OLD.id; END
            """);
    }

    // The legacy writer persisted os.stat's decimal volume and 128-bit file index.
    // Read FILE_ID_INFO directly so this compatibility check is independent of StoragePaths.Identity.
    internal static string DirectoryIdentity(string path)
    {
        const uint openExisting = 3, backupSemantics = 0x02000000;
        using var directory = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete,
            0, openExisting, backupSemantics, 0);
        if (directory.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        const int fileIdInfo = 18;
        byte[] info = new byte[24];
        if (!GetFileInformationByHandleEx(directory, fileIdInfo, info, (uint)info.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong volume = BinaryPrimitives.ReadUInt64LittleEndian(info);
        var fileId = new BigInteger(info.AsSpan(8), isUnsigned: true, isBigEndian: false);
        return FormattableString.Invariant($"{volume}:{fileId}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share,
        nint attributes, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        [Out] byte[] info, uint size);
}
