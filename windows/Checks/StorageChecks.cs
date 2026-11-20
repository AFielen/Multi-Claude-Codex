using MultiClaude;
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
        string managedPath = Path.Combine(home, "dados", "Arbeit");
        string externalPath = Path.Combine(root, "external Claude");
        foreach (string path in new[] { managedPath, externalPath })
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "Local State"), "{}");
            File.WriteAllText(Path.Combine(path, "config.json"), "{}");
        }
        using (var db = new Sqlite(Path.Combine(home, "perfis.db")))
        {
            LegacyCatalogFixture.CreateSchema(db);
            string workKey = new string('a', 32);
            db.Run("INSERT INTO perfis(id,nome,pasta) VALUES(17,'Arbeit',?), (44,'Privat',?)",
                @"dados\Arbeit", externalPath);
            db.Run("""
                INSERT INTO profile_options VALUES
                    (17,?,5,'#336699','Ctrl+Alt+4',1,1,0,'1680000000',1),
                    (44,?,2,'#ff7733','',0,0,0,NULL,0)
                """, workKey, new string('b', 32));
            db.Run("""
                INSERT INTO config VALUES ('tema','escuro'), ('idioma','de'), ('selected_code_key',?),
                    ('future','untouched'), ('apagar_perfil_17',?)
                """, workKey, LegacyCatalogFixture.DirectoryIdentity(managedPath));
        }

        Check(Catalog.FindInExisting(home, new string('a', 32))?.Provider == ProfileProvider.Claude,
            "A pre-provider shortcut resolves read-only before migration");
        var catalog = new Catalog(home);
        var all = catalog.Profiles();
        Check(all.Count == 2 && all[0].Id == 44 && all[1].Id == 17 && all.All(p => p.Provider == ProfileProvider.Claude),
            "2.3.0 order, IDs and Claude provider");
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
        Check(Catalog.FindInExisting(oldPath, work.CodeKey) == null, "Pre-options catalog has no shortcuts before migration");
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
            var link = CodeLauncher.PowerShell(
                "New-Item -ItemType Junction -Path $env:FIXTURE_JUNCTION -Target $env:FIXTURE_TARGET -ErrorAction Stop | Out-Null");
            link.Environment["FIXTURE_JUNCTION"] = destination;
            link.Environment["FIXTURE_TARGET"] = source;
            Check(CodeLauncher.RunQuiet(link, out _), "Could not create test junction at " + destination);
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
        Providers(root);
    }

    private static void Providers(string root)
    {
        var source = new Catalog(Path.Combine(root, "mixed providers"));
        var claude = source.Save(source.Draft() with { Name = "Claude" });
        Directory.CreateDirectory(claude.DesktopPath);
        File.WriteAllText(Path.Combine(claude.DesktopPath, "config.json"), "{}");
        File.WriteAllText(Path.Combine(claude.DesktopPath, "Local State"), "{}");
        var codex = source.Save(source.Draft() with { Name = "Codex", Provider = ProfileProvider.Codex });
        Check(codex.Isolated && !codex.Sharing && codex.DesktopPath == Path.Combine(source.Home, "codex-desktop", codex.CodeKey) &&
            Catalog.FindInExisting(source.Home, codex.CodeKey) == codex, "Persistent Codex identity and shortcut");
        Check(new Catalog(source.Home).Profiles().SequenceEqual(new[] { claude, codex }), "Mixed catalog survives restart");
        source.ValidateDesktop(codex);
        bool Rejected(Action operation)
        {
            try { operation(); return false; }
            catch (Exception e) when (e is ArgumentException or IOException) { return true; }
        }
        Check(Rejected(() => source.Save(claude with { Provider = ProfileProvider.Codex })) &&
              Rejected(() => source.Save(codex with { Provider = ProfileProvider.Claude })) &&
              Rejected(() => source.Save(codex with { Sharing = true })) &&
              Rejected(() => source.Save(codex with { Isolated = false })) &&
              Rejected(() => source.Save(source.Draft() with { Name = "Unknown", Provider = (ProfileProvider)27 })),
              "Provider is immutable and Codex always uses separate storage");

        var merger = new Catalog(Path.Combine(root, "provider import"));
        string database = Path.Combine(source.Home, "perfis.db");
        Check(merger.ImportDatabase(database) == 2 && merger.ImportDatabase(database) == 0,
            "Import supports mixed catalogs and unopened Codex profiles");
        var imported = merger.Profiles().Single(p => p.Provider == ProfileProvider.Codex);
        Check(imported.CodeHome == source.Home && imported.CodeKey == codex.CodeKey && imported.DesktopPath == codex.DesktopPath,
            "Imported Codex registration retains both source locations");
        merger.ValidateDesktop(imported);
        Directory.CreateDirectory(codex.DesktopPath);
        Check(!source.CanErase(codex), "Unmarked Codex data retained");
        File.WriteAllText(Path.Combine(codex.DesktopPath, "Local State"), "{}");
        File.WriteAllText(Path.Combine(codex.DesktopPath, "config.json"), "{}");
        string codexHome = Directory.CreateDirectory(Path.Combine(source.Home, "codex-home", codex.CodeKey)).FullName;
        File.WriteAllText(Path.Combine(codexHome, "Local State"), "{}");
        File.WriteAllText(Path.Combine(codexHome, "config.json"), "{}");
        var outsider = new Catalog(Path.Combine(root, "folder import"));
        Check(outsider.ImportFolders([codex.DesktopPath, codexHome]) == 0 &&
            outsider.Discover([codex.DesktopPath, codexHome]).Count == 0, "Claude discovery excludes managed Codex folders from every catalog");
        Check(!merger.CanErase(imported) && source.CanErase(codex), "Only the owning catalog can erase a marked Codex desktop");
        Check(Rejected(() => source.ValidateDesktop(codex with { DesktopPath = claude.DesktopPath })) &&
              Rejected(() => source.ValidateDesktop(claude with { DesktopPath = codex.DesktopPath })),
              "Provider paths cannot be used interchangeably");

        using (var db = new Sqlite(database))
        {
            db.Run("UPDATE perfis SET pasta=? WHERE id=?", claude.StoredPath, codex.Id);
            Check(Rejected(() => outsider.ImportDatabase(database)) && outsider.Profiles().Count == 0,
                "Import rejects conflicting provider paths atomically");
            db.Run("UPDATE perfis SET pasta=? WHERE id=?", codex.StoredPath, codex.Id);
            db.Run("UPDATE profile_options SET provider='future' WHERE profile=?", codex.Id);
            Check(Rejected(() => outsider.ImportDatabase(database)) && Rejected(() => Catalog.FindInExisting(source.Home, codex.CodeKey)),
                "Unknown on-disk providers cannot launch or import as Claude");
            db.Run("UPDATE profile_options SET provider='codex' WHERE profile=?", codex.Id);
        }
        string fakeRoaming = Path.Combine(root, "fake roaming");
        string fakeUser = Path.Combine(root, "fake user");
        string? previousRoaming = Environment.GetEnvironmentVariable("APPDATA");
        string? previousUser = Environment.GetEnvironmentVariable("USERPROFILE");
        string? previousCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("APPDATA", fakeRoaming);
            Environment.SetEnvironmentVariable("USERPROFILE", fakeUser);
            string customHome = Path.Combine(fakeUser, "custom CLI");
            Environment.SetEnvironmentVariable("CODEX_HOME", customHome);
            foreach (string path in new[] { Path.Combine(fakeRoaming, "Codex"), Path.Combine(fakeRoaming, "ChatGPT"), Path.Combine(fakeUser, ".codex"), customHome })
            {
                string global = Directory.CreateDirectory(path).FullName;
                File.WriteAllText(Path.Combine(global, "config.json"), "{}");
                File.WriteAllText(Path.Combine(global, "Local State"), "{}");
                Check(outsider.ImportFolders([global]) == 0 && Rejected(() => outsider.ValidateDesktop(claude with { DesktopPath = global })),
                    "Global provider storage is excluded: " + path);
                Check(Rejected(() => outsider.ValidateDesktop(codex with { CodeHome = global, DesktopPath = Path.Combine(global, "codex-desktop", codex.CodeKey) })),
                    "Codex cannot use a managed-looking folder under global storage: " + path);
            }
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            source.ValidateDesktop(codex); // A copied command may run inside this profile's own terminal.
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", previousRoaming);
            Environment.SetEnvironmentVariable("USERPROFILE", previousUser);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodex);
        }
        merger.Delete(imported, true);
        Check(Directory.Exists(codex.DesktopPath), "Unregistering an imported Codex profile preserves files");
        source.Delete(codex, true);
        Check(!Directory.Exists(codex.DesktopPath) && Directory.Exists(codexHome), "Erase retains Codex home and removes only the owned desktop");
    }
}
