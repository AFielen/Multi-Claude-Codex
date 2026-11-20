namespace MultiClaude;

public sealed partial class Catalog
{
    private static string UserFolder(string variable, Environment.SpecialFolder fallback)
    {
        string configured = StoragePaths.Resolve(Environment.GetEnvironmentVariable(variable));
        return configured.Length > 0 ? configured : Environment.GetFolderPath(fallback);
    }

    private bool Protected(string folder, bool codexProfile = false)
    {
        string? physical = StoragePaths.PhysicalPath(folder);
        if (physical == null) return true;
        folder = physical;
        string local = UserFolder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
        string roaming = UserFolder("APPDATA", Environment.SpecialFolder.ApplicationData);
        string user = UserFolder("USERPROFILE", Environment.SpecialFolder.UserProfile);
        // Reserve both managed trees in every catalog, including catalogs linked from another home.
        string basePath = codexProfile ? Path.GetDirectoryName(Path.GetDirectoryName(folder))! : folder;
        string? child = null;
        for (var node = new DirectoryInfo(basePath); node.Parent != null; node = node.Parent)
        {
            if ((StoragePaths.Comparer.Equals(node.Name, "codex-home") || StoragePaths.Comparer.Equals(node.Name, "codex-desktop")) &&
                (ValidKey(child) || StoragePaths.Comparer.Equals(node.Parent.FullName, Home) || File.Exists(Path.Combine(node.Parent.FullName, "perfis.db"))))
                return true;
            child = node.Name;
        }
        foreach (string ancestor in new[] { Home, Data, Pictures, user })
            if (StoragePaths.Contains(folder, StoragePaths.PhysicalPath(ancestor) ?? ancestor)) return true;
        var forbidden = new[] { Pictures, Path.Combine(Home, "claude-code"), Path.Combine(Home, "profile_shortcuts"),
            Path.Combine(roaming, "Claude"), Path.Combine(local, "Claude"), Path.Combine(local, "Claude-Data"), Path.Combine(local, "AnthropicClaude"),
            Path.Combine(user, ".codex"),
            Path.Combine(roaming, "Codex"), Path.Combine(roaming, "ChatGPT"), Path.Combine(local, "Codex"), Path.Combine(local, "ChatGPT"),
            Path.Combine(roaming, "OpenAI", "Codex"), Path.Combine(roaming, "OpenAI", "ChatGPT"),
            Path.Combine(local, "OpenAI", "Codex"), Path.Combine(local, "OpenAI", "ChatGPT") };
        foreach (string root in forbidden)
        {
            string resolved = StoragePaths.PhysicalPath(root) ?? root;
            if (StoragePaths.Contains(resolved, folder) || StoragePaths.Contains(folder, resolved)) return true;
        }
        string configured = StoragePaths.Resolve(Environment.GetEnvironmentVariable("CODEX_HOME"));
        string inherited = StoragePaths.PhysicalPath(configured) ?? configured;
        bool currentTerminalHome = codexProfile && StoragePaths.Comparer.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "codex-home") &&
            StoragePaths.Comparer.Equals(folder, inherited);
        if (!currentTerminalHome &&
            (StoragePaths.Contains(inherited, folder) || StoragePaths.Contains(folder, inherited))) return true;
        string packages = Path.Combine(local, "Packages");
        packages = StoragePaths.PhysicalPath(packages) ?? packages;
        if (!StoragePaths.Contains(packages, folder)) return false;
        string package = Path.GetRelativePath(packages, folder).Split(Path.DirectorySeparatorChar)[0];
        return new[] { "claude", "codex", "chatgpt" }.Any(name => package.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    internal bool Recognized(string folder) => StoragePaths.Ordinary(folder) && !Protected(folder) &&
        new[] { "config.json", "Local State" }.All(name =>
        {
            var marker = Path.Combine(folder, name);
            return StoragePaths.Ordinary(marker) && File.Exists(marker);
        });

    private bool ManagedChild(string path)
    {
        string? target = StoragePaths.PhysicalPath(path);
        string? parent = StoragePaths.Ordinary(Data) ? StoragePaths.PhysicalPath(Data) : null;
        return target != null && parent != null && StoragePaths.Comparer.Equals(Path.GetDirectoryName(target), parent);
    }

    private void ValidateFolder(string path)
    {
        StoragePaths.RequireOrdinary(path);
        if (Protected(path) || File.Exists(path) || (!ManagedChild(path) && !Recognized(path)))
            throw new IOException($"Invalid profile folder: {path}");
    }

    private void ValidateCodexFolders(Profile profile)
    {
        if (!ValidKey(profile.CodeKey) || !profile.Isolated || profile.Sharing)
            throw new IOException("Invalid Codex profile identity or settings.");
        StoragePaths.RequireOrdinary(profile.CodeHome);
        string desktop = Path.Combine(profile.CodeHome, "codex-desktop", profile.CodeKey);
        if (!StoragePaths.Comparer.Equals(profile.DesktopPath, desktop))
            throw new IOException("The Codex desktop folder must match its profile key and catalog.");
        foreach (string folder in new[] { desktop, Path.Combine(profile.CodeHome, "codex-home", profile.CodeKey) })
        {
            StoragePaths.RequireOrdinary(folder);
            if (Protected(folder, codexProfile: true) || File.Exists(folder))
                throw new IOException($"Invalid Codex profile folder: {folder}");
        }
    }

    public void ValidateDesktop(Profile profile)
    {
        switch (profile.Provider)
        {
            case ProfileProvider.Claude: ValidateFolder(profile.DesktopPath); break;
            case ProfileProvider.Codex: ValidateCodexFolders(profile); break;
            default: throw new ArgumentException("Unsupported profile provider.");
        }
    }

    private string NewDesktopFolder(Profile profile, HashSet<string> registered)
    {
        if (profile.Provider == ProfileProvider.Codex)
        {
            string desktop = Path.Combine(Home, "codex-desktop", profile.CodeKey);
            ValidateCodexFolders(profile with { DesktopPath = desktop });
            if (registered.Contains(StoragePaths.PhysicalPath(desktop) ?? desktop) || Path.Exists(desktop) ||
                Path.Exists(Path.Combine(Home, "codex-home", profile.CodeKey)))
                throw new IOException("Codex storage already exists for this profile key.");
            return desktop;
        }
        StoragePaths.RequireOrdinary(Data);
        string slug = StoragePaths.Slug(profile.Name.Trim());
        string folder = Path.Combine(Data, slug);
        int suffix = 2;
        while (Path.Exists(folder) || registered.Contains(StoragePaths.PhysicalPath(folder) ?? folder))
            folder = Path.Combine(Data, $"{slug}_{suffix++}");
        ValidateFolder(folder);
        return folder;
    }

    private static HashSet<string> RegisteredPaths(IEnumerable<Profile> profiles) =>
        profiles.Select(p => StoragePaths.PhysicalPath(p.DesktopPath) ?? p.DesktopPath).ToHashSet(StoragePaths.Comparer);

    private bool NewRecognizedPath(HashSet<string> known, string path)
    {
        // An existing catalog may contain a local junction alias. Only compare that identity;
        // newly discovered registrations must still pass validation using the original path.
        string? physical = StoragePaths.PhysicalPath(path);
        return physical != null && !known.Contains(physical) && Recognized(path) && known.Add(physical);
    }

    public List<string> Discover(IEnumerable<string>? running = null)
    {
        var found = RegisteredPaths(Profiles());
        var candidates = new List<string>(running ?? []);
        foreach (var location in new[] { Data, Path.Combine(UserFolder("USERPROFILE", Environment.SpecialFolder.UserProfile), "ClaudePerfis") })
        {
            if (!StoragePaths.Ordinary(location) || !Directory.Exists(location)) continue;
            try { candidates.AddRange(Directory.GetDirectories(location)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return candidates.Select(path => StoragePaths.Resolve(path)).Where(path => NewRecognizedPath(found, path)).ToList();
    }

    private static string UniqueName(Sqlite db, string name)
    {
        string result = name;
        for (int i = 2; db.Scalar("SELECT 1 FROM perfis WHERE nome=?", result) != null; i++)
            result = $"{name} ({i})";
        return result;
    }

    private long AddLinked(Sqlite db, Profile input)
    {
        string name = UniqueName(db, input.Name);
        var folder = input.DesktopPath;
        var stored = StoragePaths.Contains(Home, folder) ? Path.GetRelativePath(Home, folder) : folder;
        db.Run("INSERT INTO perfis(id,nome,pasta,foto,criado) VALUES(?,?,?,?,COALESCE(?,CURRENT_TIMESTAMP))",
            input.Id == 0 ? null : input.Id, name, stored, input.Photo, input.Created);
        long id = db.LastId;
        InsertOptions(db, id, input);
        if (!StoragePaths.Comparer.Equals(input.CodeHome, Home)) Set(db, "wpf_code_home_" + input.CodeKey, input.CodeHome);
        return id;
    }

    public int ImportFolders(IEnumerable<string> folders)
    {
        using var db = Open();
        return db.Transaction(() =>
        {
            var all = List(db, Home);
            var existing = RegisteredPaths(all);
            int count = 0;
            foreach (var input in folders)
            {
                string path = StoragePaths.Resolve(input);
                if (!NewRecognizedPath(existing, path)) continue;
                var draft = NewDraft(all, Home) with { Name = Path.GetFileName(path), DesktopPath = path };
                AddLinked(db, draft);
                all.Add(draft);
                count++;
            }
            return count;
        });
    }

    public int ImportDatabase(string file)
    {
        if (!StoragePaths.RequireUnsharedFile(file)) throw new FileNotFoundException("Catalog not found.", file);
        if (StoragePaths.Comparer.Equals(StoragePaths.PhysicalPath(file), StoragePaths.PhysicalPath(Database))) return 0;
        string source = Path.GetDirectoryName(StoragePaths.Resolve(file))!;
        using var origin = new Sqlite(file, readOnly: true);
        bool modern = Has(origin, "profile_options", "code_key");
        var imports = new List<Profile>();
        if (modern) imports.AddRange(List(origin, source, historical: true));
        string photo = Has(origin, "perfis", "foto") ? "foto" : "NULL";
        string identifier = Has(origin, "perfis", "id") ? "id" : "NULL";
        string created = Has(origin, "perfis", "criado") ? "criado" : "NULL";
        string missing = modern ? "AND NOT EXISTS(SELECT 1 FROM profile_options WHERE profile=id)" : "";
        foreach (var r in origin.Query($"SELECT nome,pasta,{photo},{identifier},{created} FROM perfis WHERE typeof(nome)='text' AND typeof(pasta)='text' {missing}"))
        {
            if (string.IsNullOrWhiteSpace(r[0]) || r[0]!.Contains('\0')) continue;
            imports.Add(NewDraft(imports, Home) with
            {
                Id = long.TryParse(r[3], out long id) ? id : 0,
                Name = r[0]!,
                DesktopPath = StoragePaths.Resolve(r[1], source),
                Photo = r[2],
                Created = r[4],
                Isolated = false,
                Shortcut = ""
            });
        }
        using var db = Open();
        return db.Transaction(() =>
        {
            var current = List(db, Home);
            var paths = RegisteredPaths(current);
            int count = 0;
            foreach (var candidate in imports)
            {
                if (candidate.Provider == ProfileProvider.Codex)
                {
                    ValidateCodexFolders(candidate);
                    string physical = StoragePaths.PhysicalPath(candidate.DesktopPath)!;
                    if (!paths.Add(physical))
                    {
                        if (current.Any(p => p.CodeKey == candidate.CodeKey && p.Provider == candidate.Provider &&
                            StoragePaths.Comparer.Equals(StoragePaths.PhysicalPath(p.DesktopPath), physical))) continue;
                        throw new IOException($"Codex desktop folder is already registered: {candidate.Name}");
                    }
                }
                else if (!NewRecognizedPath(paths, candidate.DesktopPath)) continue;
                if (!ValidKey(candidate.CodeKey) || current.Any(p => p.CodeKey == candidate.CodeKey) || candidate.CodeHome.Length == 0)
                    throw new IOException($"Profile identity conflict: {candidate.Name}");
                // Duplicate numeric row IDs are database-local. Persistent UUIDs and both account paths remain exact.
                var record = candidate with
                {
                    Id = current.Any(p => p.Id == candidate.Id) ? 0 : candidate.Id,
                    Photo = PhotoFile(candidate.Photo) == null ? null : candidate.Photo
                };
                long id = AddLinked(db, record);
                current.Add(record with { Id = id });
                count++;
            }
            return count;
        });
    }

    private string? EraseIdentity(Sqlite db, Profile p, List<Profile> all)
    {
        string path = p.DesktopPath;
        if (!all.Any(x => x.Id == p.Id && x.CodeKey == p.CodeKey && x.StoredPath == p.StoredPath && x.Provider == p.Provider &&
                          StoragePaths.Comparer.Equals(x.CodeHome, p.CodeHome) && StoragePaths.Comparer.Equals(x.DesktopPath, path)) ||
            !StoragePaths.Ordinary(path) || !Directory.Exists(path)) return null;
        if (p.Provider == ProfileProvider.Codex)
        {
            if (!StoragePaths.Comparer.Equals(Home, p.CodeHome)) return null;
            try { ValidateCodexFolders(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        else if (!ManagedChild(path) || Protected(path)) return null;
        string? target = StoragePaths.PhysicalPath(path);
        if (target == null) return null;
        foreach (var other in all.Where(x => x.Id != p.Id && x.DesktopPath.Length != 0))
        {
            string? registered = StoragePaths.PhysicalPath(other.DesktopPath);
            if (registered == null || StoragePaths.Contains(registered, target) || StoragePaths.Contains(target, registered)) return null;
        }
        string identity = StoragePaths.Identity(path);
        string? pending = db.Scalar("SELECT valor FROM config WHERE chave=?", $"apagar_perfil_{p.Id}");
        if (pending != null) return pending == identity ? identity : null;
        if (p.Provider == ProfileProvider.Claude) return Recognized(path) ? identity : null;
        try { return StoragePaths.RequireUnsharedFile(Path.Combine(path, "Local State")) ? identity : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool CanErase(Profile p)
    {
        using var db = Open();
        return EraseIdentity(db, p, List(db, Home)) != null;
    }

    // Commit the durable retry identity first. Re-check folder identity and ownership under the catalog write lock.
    private void Erase(Sqlite db, Profile p, Action<Profile>? beforeErase = null)
    {
        string? id = EraseIdentity(db, p, List(db, Home));
        if (id == null) return;
        beforeErase?.Invoke(p);
        db.Transaction(() =>
        {
            if (EraseIdentity(db, p, List(db, Home)) != id) throw new IOException("Profile folder ownership changed.");
            Set(db, $"apagar_perfil_{p.Id}", id);
            return 0;
        });
        db.Transaction(() =>
        {
            if (EraseIdentity(db, p, List(db, Home)) != id) throw new IOException("Profile folder ownership changed.");
            Directory.Delete(p.DesktopPath, recursive: true);
            return 0;
        });
    }

    public void Delete(Profile p, bool erase)
    {
        using var db = Open();
        if (erase) Erase(db, p);
        db.Transaction(() =>
        {
            if (List(db, Home, p.CodeKey).SingleOrDefault() is { } current && current.Id == p.Id &&
                current.StoredPath == p.StoredPath && current.Provider == p.Provider)
            {
                db.Run("DELETE FROM perfis WHERE id=?", p.Id);
                Set(db, $"apagar_perfil_{p.Id}", null);
                Set(db, "wpf_code_home_" + p.CodeKey, null);
                db.Run("DELETE FROM config WHERE chave='selected_code_key' AND valor=?", p.CodeKey);
            }
            return 0;
        });
    }

    public void Reset(Action<Profile>? beforeErase = null)
    {
        using var db = Open();
        var profiles = List(db, Home);
        foreach (var p in profiles) Erase(db, p, beforeErase);
        db.Transaction(() =>
        {
            var live = List(db, Home);
            if (!profiles.SequenceEqual(live)) throw new IOException("Catalog changed during reset.");
            if (StoragePaths.Ordinary(Pictures) && Directory.Exists(Pictures))
                foreach (var path in Directory.EnumerateFiles(Pictures, "usuario_*"))
                    if (ImageExtension(path) && StoragePaths.Ordinary(path)) File.Delete(path);
            foreach (string log in new[] { "log.txt", "log.txt.old" })
            {
                string path = Path.Combine(Home, log);
                if (StoragePaths.RequireUnsharedFile(path)) File.Delete(path);
            }
            db.Run("DELETE FROM perfis");
            db.Run("DELETE FROM profile_options");
            db.Run("DELETE FROM config");
            if (Has(db, "open_requests", "code_key")) db.Run("DELETE FROM open_requests");
            return 0;
        });
    }
}
