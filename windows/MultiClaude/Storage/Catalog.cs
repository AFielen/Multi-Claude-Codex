using System.Globalization;
using System.Text.RegularExpressions;

namespace MultiClaude;

public enum ProfileProvider { Claude, Codex }

public sealed record Profile(long Id, string Name, string StoredPath, string DesktopPath, string? Photo,
    string CodeKey, int Position, string Color, string Shortcut, bool Isolated, bool Border, bool Sharing,
    string? LastOpen, bool Tray)
{
    public ProfileProvider Provider { get; init; } = ProfileProvider.Claude;
    public string CodeHome { get; init; } = "";
    public string? Created { get; init; }
    public override string ToString() => Name;
}

public sealed partial class Catalog
{
    private static readonly string[] Colors = ["#d97757", "#6a9bd8", "#5fa85e", "#b77fd0", "#d39b33", "#4fb0a8", "#d86a8f"];
    internal static bool ValidKey(string? key) => key is not null && Regex.IsMatch(key, @"\A[a-f0-9]{32}\z");
    public string Home { get; }
    private string Database => Path.Combine(Home, "perfis.db");
    private string Data => Path.Combine(Home, "dados");
    private string Pictures => Path.Combine(Home, "profile_pictures");
    private Sqlite Open() => new(Database);

    public Catalog(string home)
    {
        Home = StoragePaths.Resolve(home);
        StoragePaths.RequireOrdinary(Home);
        Directory.CreateDirectory(Home);
        Home = StoragePaths.PhysicalPath(Home) ?? throw new IOException("Cannot resolve the catalog directory.");
        using var db = Open();
        db.Transaction(() =>
        {
            db.Run("""
                CREATE TABLE IF NOT EXISTS perfis(id INTEGER PRIMARY KEY, nome TEXT UNIQUE NOT NULL, pasta TEXT NOT NULL, criado TEXT DEFAULT CURRENT_TIMESTAMP)
                """);
            db.Run("CREATE TABLE IF NOT EXISTS config(chave TEXT PRIMARY KEY, valor TEXT)");
            if (!Has(db, "perfis", "foto")) db.Run("ALTER TABLE perfis ADD COLUMN foto TEXT");
            db.Run("""
                CREATE TABLE IF NOT EXISTS profile_options(
                    profile INTEGER PRIMARY KEY REFERENCES perfis(id) ON DELETE CASCADE, code_key TEXT UNIQUE NOT NULL,
                    position INTEGER NOT NULL, color TEXT NOT NULL, shortcut TEXT NOT NULL, isolated INTEGER NOT NULL,
                    border INTEGER NOT NULL DEFAULT 0, sharing INTEGER NOT NULL DEFAULT 0, last_open TEXT, tray INTEGER NOT NULL DEFAULT 0)
                """);
            if (!Has(db, "profile_options", "tray")) db.Run("ALTER TABLE profile_options ADD COLUMN tray INTEGER NOT NULL DEFAULT 0");
            if (!Has(db, "profile_options", "provider")) db.Run("ALTER TABLE profile_options ADD COLUMN provider TEXT NOT NULL DEFAULT 'claude'");
            db.Run("""
                CREATE TRIGGER IF NOT EXISTS remove_profile_options AFTER DELETE ON perfis
                BEGIN DELETE FROM profile_options WHERE profile=OLD.id; END
                """);
            foreach (var row in db.Query("SELECT id FROM perfis LEFT JOIN profile_options ON profile=id WHERE profile IS NULL ORDER BY id"))
            {
                var draft = NewDraft(List(db, Home), Home) with { Isolated = false, Shortcut = "" };
                InsertOptions(db, long.Parse(row[0]!, CultureInfo.InvariantCulture), draft);
            }
            return 0;
        });
    }

    private static bool Has(Sqlite db, string table, string column) =>
        db.Query($"PRAGMA table_info({table})").Any(r => r[1] == column);

    private static string ProviderValue(Profile p) => p.Provider switch
    {
        ProfileProvider.Claude => "claude",
        ProfileProvider.Codex when p.Isolated && !p.Sharing => "codex",
        ProfileProvider.Codex => throw new ArgumentException("Codex profiles require separate settings and sign-in."),
        _ => throw new ArgumentException("Unsupported profile provider.")
    };

    private static List<Profile> List(Sqlite db, string home, string? key = null, bool historical = false)
    {
        string photo = historical && !Has(db, "perfis", "foto") ? "NULL" : "foto";
        string created = historical && !Has(db, "perfis", "criado") ? "NULL" : "criado";
        string tray = historical && !Has(db, "profile_options", "tray") ? "0" : "tray";
        string provider = historical && !Has(db, "profile_options", "provider") ? "'claude'" : "provider";
        string codeHome = historical && !Has(db, "config", "valor") ? "NULL" : "(SELECT valor FROM config WHERE chave='wpf_code_home_' || code_key)";
        string sql = $"""
            SELECT id,nome,pasta,{photo},code_key,position,color,shortcut,isolated,border,sharing,last_open,{tray},{created},{codeHome},{provider}
            FROM perfis JOIN profile_options ON profile=id
            """;
        var rows = key == null ? db.Query(sql + " ORDER BY position,id") : db.Query(sql + " WHERE code_key=?", key);
        return rows.Select(r => new Profile(long.Parse(r[0]!, CultureInfo.InvariantCulture), r[1]!, r[2] ?? "", StoragePaths.Resolve(r[2], home), r[3],
            r[4]!, int.Parse(r[5]!, CultureInfo.InvariantCulture), r[6]!, r[7]!, r[8] != "0", r[9] != "0", r[10] != "0", r[11], r[12] != "0")
        {
            Created = r[13], CodeHome = r[14] is null ? home : StoragePaths.Resolve(r[14]),
            Provider = r[15] switch
            {
                "claude" => ProfileProvider.Claude,
                "codex" when r[8] == "1" && r[10] == "0" => ProfileProvider.Codex,
                "codex" => throw new IOException("Invalid Codex profile settings."),
                _ => throw new IOException("Unsupported profile provider.")
            }
        }).ToList();
    }

    public List<Profile> Profiles() { using var db = Open(); return List(db, Home); }

    public static Profile? FindInExisting(string home, string key)
    {
        home = StoragePaths.Resolve(home);
        if (home.Length == 0 || !ValidKey(key)) return null;
        var file = Path.Combine(home, "perfis.db");
        if (!StoragePaths.RequireUnsharedFile(file)) return null;
        using var db = new Sqlite(file, readOnly: true);
        if (!Has(db, "profile_options", "code_key")) return null;
        return List(db, home, key, historical: true).FirstOrDefault();
    }

    public string? Get(string key) { using var db = Open(); return db.Scalar("SELECT valor FROM config WHERE chave=?", key); }
    public void Set(string key, string? value) { using var db = Open(); Set(db, key, value); }

    private static void Set(Sqlite db, string key, string? value)
    {
        if (value == null) db.Run("DELETE FROM config WHERE chave=?", key);
        else db.Run("INSERT OR REPLACE INTO config(chave,valor) VALUES(?,?)", key, value);
    }

    public Profile Draft() => NewDraft(Profiles(), Home);
    private static Profile NewDraft(List<Profile> all, string home)
    {
        var tint = Colors.FirstOrDefault(c => all.All(p => !StoragePaths.Comparer.Equals(c, p.Color))) ?? Colors[all.Count % Colors.Length];
        var shortcut = Enumerable.Range(1, 9).Select(i => $"Ctrl+Alt+{i}").FirstOrDefault(s => all.All(p => p.Shortcut != s)) ?? "";
        return new(0, "", "", "", null, Guid.NewGuid().ToString("N"), 0, tint, shortcut, true, false, false, null, false) { CodeHome = home };
    }

    private static void InsertOptions(Sqlite db, long id, Profile p) =>
        db.Run("""
            INSERT INTO profile_options(profile,code_key,position,color,shortcut,isolated,border,sharing,last_open,tray,provider)
            VALUES(?,?,(SELECT COALESCE(MAX(position),-1)+1 FROM profile_options),?,?,?,?,?,?,?,?)
            """, id, p.CodeKey, p.Color, p.Shortcut, p.Isolated, p.Border, p.Isolated && p.Sharing, p.LastOpen, p.Tray, ProviderValue(p));

    public Profile Save(Profile draft, Action<Profile>? beforeCommit = null)
    {
        string name = draft.Name.Trim();
        if (name.Length == 0 || name.Contains('\0')) throw new ArgumentException("Enter a profile name.");
        if (!Regex.IsMatch(draft.Color, @"\A#[0-9a-fA-F]{6}\z")) throw new ArgumentException("Invalid profile color.");
        if (!ValidKey(draft.CodeKey)) throw new ArgumentException("Invalid profile identity.");
        _ = ProviderValue(draft);
        using var db = Open();
        return db.Transaction(() =>
        {
            var all = List(db, Home);
            var original = all.SingleOrDefault(p => p.Id == draft.Id && p.CodeKey == draft.CodeKey);
            if (draft.Id != 0 && original == null) throw new IOException("Profile changed or was removed.");
            if (original != null && original.Provider != draft.Provider) throw new ArgumentException("A profile's provider cannot be changed.");
            if (draft.Shortcut.Length > 0 && all.Any(p => p.Id != draft.Id && p.Shortcut.Equals(draft.Shortcut, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The shortcut is already assigned to a profile.");
            long id = draft.Id;
            if (original == null)
            {
                string folder = NewDesktopFolder(draft with { CodeHome = Home }, RegisteredPaths(all));
                db.Run("INSERT INTO perfis(nome,pasta,foto) VALUES (?,?,?)", name, Path.GetRelativePath(Home, folder), draft.Photo);
                id = db.LastId;
                InsertOptions(db, id, draft);
            }
            else
            {
                db.Run("UPDATE perfis SET nome=?, foto=? WHERE id=?", name, draft.Photo, id);
                db.Run("""
                    UPDATE profile_options SET color=?,shortcut=?,isolated=?,border=?,sharing=?,tray=? WHERE profile=?
                    """, draft.Color, draft.Shortcut, draft.Isolated, draft.Border, draft.Isolated && draft.Sharing, draft.Tray, id);
            }
            Set(db, "selected_code_key", draft.CodeKey);
            var saved = List(db, Home, draft.CodeKey).Single();
            beforeCommit?.Invoke(saved);
            return saved;
        });
    }

    public void Move(long source, long target)
    {
        using var db = Open();
        db.Transaction(() =>
        {
            var order = List(db, Home).Select(p => p.Id).ToList();
            int position = order.IndexOf(target);
            if (source == target || position < 0 || !order.Remove(source)) return 0;
            order.Insert(position, source);
            for (int i = 0; i < order.Count; i++) db.Run("UPDATE profile_options SET position=? WHERE profile=?", i, order[i]);
            return 0;
        });
    }

    public void MarkOpened(Profile p)
    {
        using var db = Open();
        db.Run("UPDATE profile_options SET last_open=strftime('%s','now') WHERE profile=? AND code_key=?", p.Id, p.CodeKey);
    }

    public void Enqueue(string codeKey)
    {
        if (!ValidKey(codeKey)) throw new ArgumentException("Invalid profile identity.");
        using var db = Open();
        db.Transaction(() =>
        {
            if (List(db, Home, codeKey).Count == 0) throw new IOException("Profile no longer exists.");
            db.Run("CREATE TABLE IF NOT EXISTS open_requests(code_key TEXT PRIMARY KEY, requested REAL)");
            db.Run("INSERT OR REPLACE INTO open_requests VALUES(?,strftime('%s','now'))", codeKey);
            return 0;
        });
    }

    public string? Dequeue()
    {
        using var db = Open();
        if (db.Scalar("SELECT name FROM sqlite_master WHERE type='table' AND name='open_requests'") == null) return null;
        return db.Transaction(() =>
        {
            var row = db.Query("SELECT code_key,requested FROM open_requests ORDER BY requested LIMIT 1").FirstOrDefault();
            if (row == null) return null;
            db.Run("DELETE FROM open_requests WHERE code_key=?", row[0]);
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            return double.TryParse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var when) && now - when >= 0 && now - when < 60 &&
                ValidKey(row[0]) && List(db, Home, row[0]).Count == 1 ? row[0] : null;
        });
    }
}
