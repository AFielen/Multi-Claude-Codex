"""Persistent display order and Claude Code identities for the portable manager."""
from dataclasses import dataclass
from uuid import uuid4


COLORS = ("#d97757", "#6a9bd8", "#5fa85e", "#b77fd0", "#d39b33", "#4fb0a8", "#d86a8f")


@dataclass(frozen=True)
class Settings:
    key: str
    color: str
    shortcut: str
    isolated: int
    border: int
    sharing: int
    last_open: str | None
    tray: int


def has_provider(con):
    return any(r[1] == "provider" for r in con.execute("PRAGMA table_info(profile_options)"))


def require_claude_catalog(con, profile_key=None):
    """The Python manager's launch, logout and reset paths only understand Claude."""
    if not has_provider(con):
        return
    sql = "SELECT 1 FROM profile_options WHERE provider IS NOT 'claude'"
    row = con.execute(sql if profile_key is None else sql + " AND code_key=?",
                      () if profile_key is None else (profile_key,)).fetchone()
    if row:
        raise OSError("Open this profile catalog with the Multi Claude Codex Windows (WPF) edition.")


def migrate(con):
    con.execute("""
        CREATE TABLE IF NOT EXISTS profile_options (
            profile INTEGER PRIMARY KEY REFERENCES perfis(id) ON DELETE CASCADE,
            code_key TEXT UNIQUE NOT NULL, position INTEGER NOT NULL, color TEXT NOT NULL,
            shortcut TEXT NOT NULL, isolated INTEGER NOT NULL, border INTEGER NOT NULL DEFAULT 0,
            sharing INTEGER NOT NULL DEFAULT 0, last_open TEXT, tray INTEGER NOT NULL DEFAULT 0)
    """)
    if "tray" not in {r[1] for r in con.execute("PRAGMA table_info(profile_options)")}:
        con.execute("ALTER TABLE profile_options ADD COLUMN tray INTEGER NOT NULL DEFAULT 0")
    # Older app versions open the same database without enabling SQLite's foreign keys.
    con.execute("""CREATE TRIGGER IF NOT EXISTS remove_profile_options AFTER DELETE ON perfis
                   BEGIN DELETE FROM profile_options WHERE profile=OLD.id; END""")
    missing = con.execute("""
        SELECT id FROM perfis LEFT JOIN profile_options ON profile=id
        WHERE profile IS NULL ORDER BY id
    """).fetchall()
    for (identifier,) in missing:
        add(con, identifier, legacy=True)


def all_settings(con):
    rows = con.execute("""
        SELECT profile, code_key, color, shortcut, isolated, border, sharing, last_open, tray
        FROM profile_options ORDER BY position, profile
    """)
    return {identifier: Settings(*fields) for identifier, *fields in rows}


def defaults(entries, legacy=False):
    colors = {item.color.lower() for item in entries.values()}
    color = next((c for c in COLORS if c not in colors), COLORS[len(entries) % len(COLORS)])
    taken = {item.shortcut for item in entries.values()}
    shortcut = "" if legacy else next(
        (f"Ctrl+Alt+{n}" for n in range(1, 10) if f"Ctrl+Alt+{n}" not in taken), "")
    return color, shortcut


def add(con, identifier, *, legacy=False):
    color, shortcut = defaults(all_settings(con), legacy)
    con.execute("""
        INSERT INTO profile_options(profile, code_key, position, color, shortcut, isolated)
        VALUES (?, ?, (SELECT COALESCE(MAX(position), -1)+1 FROM profile_options), ?, ?, ?)
    """, (identifier, uuid4().hex, color, shortcut, int(not legacy)))


def move(con, source, target):
    order = list(all_settings(con))
    if source not in order or target not in order or source == target:
        return
    destination = order.index(target)
    order.remove(source)
    order.insert(destination, source)
    con.executemany("UPDATE profile_options SET position=? WHERE profile=?", enumerate(order))
