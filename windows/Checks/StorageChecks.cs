using MultiClaude;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class StorageChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string path, StringBuilder result, uint capacity);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static void Run(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "storage suite");
        string home = Path.Combine(root, "catalog");
        Directory.CreateDirectory(home);
        // Python supplies a real pre-existing SQLite catalog in the 2.3.0 schema.
        string script = """
            import sqlite3, sys, pathlib, os
            home = pathlib.Path(sys.argv[1])
            db = sqlite3.connect(home/'perfis.db')
            db.executescript('''
            CREATE TABLE perfis(id INTEGER PRIMARY KEY, nome TEXT UNIQUE NOT NULL, pasta TEXT NOT NULL, criado TEXT DEFAULT CURRENT_TIMESTAMP, foto TEXT);
            CREATE TABLE config(chave TEXT PRIMARY KEY, valor TEXT);
            CREATE TABLE profile_options(profile INTEGER PRIMARY KEY REFERENCES perfis(id) ON DELETE CASCADE, code_key TEXT UNIQUE NOT NULL, position INTEGER NOT NULL, color TEXT NOT NULL, shortcut TEXT NOT NULL, isolated INTEGER NOT NULL, border INTEGER NOT NULL DEFAULT 0, sharing INTEGER NOT NULL DEFAULT 0, last_open TEXT, tray INTEGER NOT NULL DEFAULT 0);
            ''')
            external = home.parent/'external Claude'
            for d in [external, home/'dados'/'Arbeit']:
                d.mkdir(parents=True)
                (d/'Local State').write_text('{}')
                (d/'config.json').write_text('{}')
            db.executemany('INSERT INTO perfis(id,nome,pasta) VALUES (?,?,?)', [(17,'Arbeit',r'dados\Arbeit'),(44,'Privat',str(external))])
            db.executemany('INSERT INTO profile_options VALUES(?,?,?,?,?,?,?,?,?,?)', [(17,'a'*32,5,'#336699','Ctrl+Alt+4',1,1,0,'1680000000',1),(44,'b'*32,2,'#ff7733','',0,0,0,None,0)])
            db.executemany('INSERT INTO config VALUES(?,?)',[('tema','escuro'),('idioma','de'),('selected_code_key','a'*32),('future','untouched')])
            info = os.stat(home/'dados'/'Arbeit')
            db.execute("INSERT INTO config VALUES('apagar_perfil_17',?)",(f'{info.st_dev}:{info.st_ino}',))
            db.commit()
            """;
        var python = new ProcessStartInfo { FileName = "python", UseShellExecute = false };
        python.ArgumentList.Add("-c"); python.ArgumentList.Add(script); python.ArgumentList.Add(home);
        using (var fixture = Process.Start(python)!)
        {
            fixture.WaitForExit();
            Check(fixture.ExitCode == 0, "Python fixture generation failed.");
        }

        var catalog = new Catalog(home);
        var all = catalog.Profiles();
        Check(all.Count == 2 && all[0].Id == 44 && all[1].Id == 17, "2.3.0 order/IDs");
        Profile work = all[1];
        Check(work.CodeKey == new string('a', 32) && work.StoredPath == @"dados\Arbeit" &&
              work.DesktopPath == Path.Combine(home, "dados", "Arbeit") && work.Border && work.Tray && work.Isolated &&
              work.LastOpen == "1680000000" && work.CodeHome == home && catalog.Get("tema") == "escuro", "2.3.0 import");
        Check(!all[0].Isolated && all[0].DesktopPath == Path.Combine(root, "external Claude"), "Shared code / external Desktop");
        catalog.ValidateDesktop(work);
        catalog.Move(17, 44);
        work = catalog.Save(work with { Name = "Büro" });
        catalog = new Catalog(home);
        Check(catalog.Profiles()[0] == work with { Position = 0 } && catalog.Get("selected_code_key") == work.CodeKey &&
              catalog.Get("future") == "untouched", "Catalog reopens with saved settings");
        catalog.Enqueue(work.CodeKey);
        Check(catalog.Dequeue() == work.CodeKey && catalog.Dequeue() == null, "Single-use shortcut request");
        using (var db = new Sqlite(Path.Combine(home, "perfis.db")))
            db.Run("INSERT INTO open_requests VALUES(?,1)", work.CodeKey);
        Check(catalog.Dequeue() == null, "Expired shortcut requests do not activate profiles");
        var failed = false;
        try { catalog.Save(catalog.Draft() with { Name = "Failed draft" }, _ => throw new IOException("conflict")); }
        catch (IOException) { failed = true; }
        Check(failed && catalog.Profiles().Count == 2, "Failed save rolled back");
        string missing = Path.Combine(root, "missing");
        Check(Catalog.FindInExisting(missing, work.CodeKey) is null && !Directory.Exists(missing), "Read-only shortcut check");
        Check(Catalog.FindInExisting(home, work.CodeKey)?.Id == work.Id, "Read-only existing profile");
        var merger = new Catalog(Path.Combine(root, "merged"));
        Check(merger.ImportDatabase(Path.Combine(home, "perfis.db")) == 2, "Import complete");
        var imported = merger.Profiles().Single(p => p.Id == 17);
        Check(imported.CodeKey == work.CodeKey && imported.CodeHome == work.CodeHome && imported.DesktopPath == work.DesktopPath,
              "Database import preserves both account paths and identifiers");
        Check(!merger.CanErase(imported), "Referenced external profile retained");
        Check(merger.ImportDatabase(Path.Combine(home, "perfis.db")) == 0, "Import is repeatable");
        var newProfile = catalog.Save(catalog.Draft() with { Name = "NUL" });
        Check(newProfile.Isolated && Path.GetFileName(newProfile.DesktopPath) == "_NUL", "New profile defaults");
        catalog.Delete(newProfile, false);
        Check(catalog.CanErase(work), "Eligible local registered folder");
        string userCode = Directory.CreateDirectory(Path.Combine(home, "claude-code", work.CodeKey)).FullName;
        string previous = Directory.CreateDirectory(Path.Combine(home, "dados", "unregistered")).FullName;
        string busy = Path.Combine(work.DesktopPath, "locked.bin");
        File.WriteAllText(busy, "test");
        bool inUse = false;
        using (File.Open(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { catalog.Delete(work, true); }
            catch (IOException) { inUse = true; }
            catch (UnauthorizedAccessException) { inUse = true; }
        }
        Check(inUse && catalog.Profiles().Any(p => p.CodeKey == work.CodeKey), "Failed deletion retains registration");
        File.Delete(Path.Combine(work.DesktopPath, "Local State"));
        Check(catalog.CanErase(work), "A partially cleared directory can be retried");
        catalog.Delete(work, true);
        Check(!Directory.Exists(work.DesktopPath), "Owned confirmed folder erased");
        catalog.Reset();
        Check(Directory.Exists(userCode) && Directory.Exists(previous), "Reset retains Code data and unregistered folders");
        Check(Directory.Exists(all[0].DesktopPath) && catalog.Profiles().Count == 0 && catalog.Get("tema") is null, "Reset preserves external profile");
        string oldPath = Path.Combine(root, "old catalog");
        Directory.CreateDirectory(oldPath);
        using (var old = new Sqlite(Path.Combine(oldPath, "perfis.db")))
        {
            old.Run("CREATE TABLE perfis(id INTEGER PRIMARY KEY, nome TEXT UNIQUE NOT NULL, pasta TEXT NOT NULL, criado TEXT DEFAULT CURRENT_TIMESTAMP)");
            old.Run("INSERT INTO perfis(id,nome,pasta) VALUES(73,'Old',?)", all[0].DesktopPath);
        }
        var migrated = new Catalog(oldPath);
        var oldProfile = migrated.Profiles().Single();
        Check(oldProfile.Id == 73 && !oldProfile.Isolated && oldProfile.Shortcut == "" && oldProfile.CodeKey.Length == 32 &&
              new Catalog(oldPath).Profiles().Single() == oldProfile, "Pre-options schema gets one persistent identity and shared mode");

        var aliasCatalog = new Catalog(Path.Combine(root, "alias catalog"));
        var managed = aliasCatalog.Save(aliasCatalog.Draft() with { Name = "Managed" });
        Directory.CreateDirectory(managed.DesktopPath);
        File.WriteAllText(Path.Combine(managed.DesktopPath, "config.json"), "{}");
        File.WriteAllText(Path.Combine(managed.DesktopPath, "Local State"), "{}");
        var alias = aliasCatalog.Save(aliasCatalog.Draft() with { Name = "Second" });
        string junction = Path.Combine(root, "mount");
        void CreateJunction(string source, string destination)
        {
            var link = new ProcessStartInfo("python") { UseShellExecute = false };
            link.ArgumentList.Add("-c"); link.ArgumentList.Add("import _winapi,sys;_winapi.CreateJunction(*sys.argv[1:])");
            link.ArgumentList.Add(source);
            link.ArgumentList.Add(destination);
            using var process = Process.Start(link)!;
            process.WaitForExit();
            Check(process.ExitCode == 0, "Junction fixture");
        }
        CreateJunction(Path.GetDirectoryName(managed.DesktopPath)!, junction);
        using (var db = new Sqlite(Path.Combine(aliasCatalog.Home, "perfis.db")))
            db.Run("UPDATE perfis SET pasta=? WHERE id=?", Path.Combine(junction, "Managed"), alias.Id);
        Check(!aliasCatalog.CanErase(managed), "Legacy junction registration protects its physical target");
        var sourceCatalog = new Catalog(Path.Combine(root, "alternate catalog"));
        Check(sourceCatalog.ImportFolders([managed.DesktopPath]) == 1, "Create second catalog for import fixture");
        aliasCatalog.Delete(managed, false);
        Check(!aliasCatalog.Discover([managed.DesktopPath]).Contains(managed.DesktopPath),
            "Discovery recognizes existing registration through a junction");
        Check(aliasCatalog.ImportFolders([managed.DesktopPath]) == 0 &&
            aliasCatalog.ImportDatabase(Path.Combine(sourceCatalog.Home, "perfis.db")) == 0,
            "Folder and database import deduplicate a junction registration");
        var existingAlias = Path.Combine(junction, "Managed");
        Check(aliasCatalog.Profiles().Single().StoredPath == existingAlias,
            "Physical comparison preserves the original path text");

        var dataParent = Path.GetDirectoryName(managed.DesktopPath)!;
        var protectedClaude = Directory.CreateDirectory(Path.Combine(dataParent, "Claude")).FullName;
        File.WriteAllText(Path.Combine(protectedClaude, "config.json"), "{}");
        File.WriteAllText(Path.Combine(protectedClaude, "Local State"), "{}");
        string? previousRoaming = Environment.GetEnvironmentVariable("APPDATA");
        try
        {
            Environment.SetEnvironmentVariable("APPDATA", junction);
            Check(!aliasCatalog.Recognized(protectedClaude), "Default Claude folder protected through redirected Roaming AppData");
        }
        finally { Environment.SetEnvironmentVariable("APPDATA", previousRoaming); }

        var reserved = aliasCatalog.Save(aliasCatalog.Draft() with { Name = "Existing name" });
        using (var db = new Sqlite(Path.Combine(aliasCatalog.Home, "perfis.db")))
            db.Run("UPDATE perfis SET pasta=? WHERE id=?", Path.Combine(junction, "unopened"), reserved.Id);
        var distinct = aliasCatalog.Save(aliasCatalog.Draft() with { Name = "unopened" });
        Check(Path.GetFileName(distinct.DesktopPath) == "unopened_2", "New profile respects an unopened directory reserved by a junction alias");

        var shortPath = new StringBuilder(32768);
        uint length = GetShortPathNameW(managed.DesktopPath, shortPath, (uint)shortPath.Capacity);
        if (length > 0 && length < shortPath.Capacity && !string.Equals(shortPath.ToString(), managed.DesktopPath, StringComparison.OrdinalIgnoreCase))
        {
            using (var db = new Sqlite(Path.Combine(aliasCatalog.Home, "perfis.db")))
                db.Run("UPDATE perfis SET pasta=? WHERE id=?", shortPath.ToString(), alias.Id);
            Check(!aliasCatalog.Discover([managed.DesktopPath]).Contains(managed.DesktopPath) &&
                aliasCatalog.ImportFolders([managed.DesktopPath]) == 0 &&
                aliasCatalog.ImportDatabase(Path.Combine(sourceCatalog.Home, "perfis.db")) == 0 &&
                aliasCatalog.Profiles().Single(p => p.Id == alias.Id).StoredPath == shortPath.ToString(),
                "DOS short aliases deduplicate without rewriting the stored spelling");
            using (var db = new Sqlite(Path.Combine(aliasCatalog.Home, "perfis.db")))
                db.Run("UPDATE perfis SET pasta=? WHERE id=?", existingAlias, alias.Id);
        }
        aliasCatalog.Reset();
        Check(File.Exists(Path.Combine(managed.DesktopPath, "Local State")), "Shared managed folder survives reset");
        Directory.Delete(junction);
        var redirected = new Catalog(Path.Combine(root, "redirected catalog"));
        string redirectedData = Path.Combine(redirected.Home, "dados");
        CreateJunction(Path.GetDirectoryName(managed.DesktopPath)!, redirectedData);
        Check(redirected.ImportFolders([managed.DesktopPath]) == 1 && !redirected.CanErase(redirected.Profiles().Single()),
            "A redirected data root cannot claim ownership of an external profile");
        redirected.Reset();
        Check(File.Exists(Path.Combine(managed.DesktopPath, "Local State")), "Reset keeps externally owned directory under a redirected data root");
        Directory.Delete(redirectedData);
    }
}
