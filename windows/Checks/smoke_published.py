"""Exercise the published win-x64 WPF app with the 2.3.0 writer and a fictional Desktop."""
import contextlib
import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import runpy
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time

REPOSITORY = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(REPOSITORY), str(REPOSITORY / "recursos")]
from smoke_profile_workflow import RECORDER, wait_for


def window_for(pid, owned=False):
    found = []
    enum = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    @enum
    def window(hwnd, _):
        current = wintypes.DWORD()
        ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(current))
        title = ctypes.create_unicode_buffer(256)
        ctypes.windll.user32.GetWindowTextW(hwnd, title, len(title))
        has_owner = bool(ctypes.windll.user32.GetWindow(wintypes.HWND(hwnd), 4))
        if (current.value == pid and has_owner == owned and
                (owned or title.value.startswith("Multi Claude")) and ctypes.windll.user32.IsWindowVisible(hwnd)):
            found.append(hwnd)
        return True

    ctypes.windll.user32.EnumWindows(window, 0)
    return found[0] if found else None


def main():
    app = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="multi-claude-wpf-smoke-") as scratch:
        root = Path(scratch)
        home, receipts = root / "Testkatalog", root / "receipts"
        user = root / "fictional user"
        local = user / "AppData/Local"
        roaming = user / "AppData/Roaming"
        desktop = local / "AnthropicClaude/claude.exe"
        for location in (receipts, roaming, desktop.parent, home):
            location.mkdir(parents=True)
        compiler = Path(os.environ["SystemRoot"], "Microsoft.NET/Framework64/v4.0.30319/csc.exe")
        source = root / "Fixture.cs"
        source.write_text(RECORDER, encoding="ascii")
        subprocess.run([str(compiler), "/nologo", "/target:winexe", f"/out:{desktop}", str(source)],
                       check=True, creationflags=subprocess.CREATE_NO_WINDOW)
        os.environ.update(USERPROFILE=str(user), APPDATA=str(roaming), LOCALAPPDATA=str(local),
                          MULTI_CLAUDE_HOME=str(home), MULTI_CLAUDE_SMOKE_RUN="wpf",
                          MULTI_CLAUDE_SMOKE_RECEIPTS=str(receipts), MULTI_CLAUDE_SMOKE_ROLE="manager")
        ref = runpy.run_path(str(REPOSITORY / "Multi Claude.pyw"))
        paths = [str(Path("dados", "Arbeit")), str(root / "external fixture")]
        for path in paths:
            location = home / path
            location.mkdir(parents=True)
            for name in ("config.json", "Local State"):
                (location / name).write_text("{}", encoding="ascii")
        with ref["db"]() as con:
            for identifier, name, path in zip((41, 87), ("Arbeit", "Privat"), paths, strict=True):
                con.execute("INSERT INTO perfis(id,nome,pasta) VALUES(?,?,?)", (identifier,name,path))
                ref["prefs"].add(con, identifier)
            con.execute("UPDATE profile_options SET shortcut=''")
            con.executemany("INSERT INTO config VALUES(?,?)", [("idioma","de"),("tema","claro"),("login_url","obsolete fixture"),("login_pasta","obsolete folder"),("login_ok","1")])
            original = con.execute("SELECT id,pasta,code_key FROM perfis JOIN profile_options ON id=profile ORDER BY id").fetchall()
        # First run from a clean unpacked package: the default catalog belongs to its executable.
        portable = root / "blank portable"
        portable.mkdir()
        shutil.copy2(app,portable/app.name)
        (portable/"gallery").mkdir()
        for bundled in (REPOSITORY/"profile_pictures").glob("*.png"):
            shutil.copy2(app.parent/"gallery"/bundled.name,portable/"gallery"/bundled.name)
        for license_file in ("LICENSE","NOTICE"):
            shutil.copy2(app.parent/license_file,portable/license_file)
        blank_env = os.environ.copy()
        blank_env.pop("MULTI_CLAUDE_HOME",None)
        blank = subprocess.Popen([str(portable / app.name)],cwd=root,env=blank_env)
        try:
            welcome = wait_for(blank,lambda:window_for(blank.pid,owned=True),"first-run language choice")
            assert (portable/"perfis.db").is_file()
            ctypes.windll.user32.PostMessageW(wintypes.HWND(welcome),0x10,0,0)
            def accepted():
                with contextlib.closing(sqlite3.connect(portable/"perfis.db")) as db:
                    return db.execute("SELECT valor FROM config WHERE chave='idioma'").fetchone()
            wait_for(blank,accepted,"persisted first-run language")
            hwnd = wait_for(blank,lambda:window_for(blank.pid),"empty portable workspace")
            ctypes.windll.user32.PostMessageW(wintypes.HWND(hwnd),0x10,0,0)
            assert blank.wait(timeout=20)==0
        finally:
            if blank.poll() is None:
                blank.kill()
                blank.wait()
        print("PASS clean portable EXE created its own empty catalog and saved first-run settings.", flush=True)
        seen = set()
        for cycle in (1, 2):
            # On the second start the shortcut itself opens the first profile from a stopped manager.
            cold_key=original[-1][2] if cycle==2 else None
            args=[str(app),"--open-profile",cold_key,"--manager-home",str(home)] if cold_key else [str(app)]
            manager = subprocess.Popen(args, cwd=root)
            try:
                hwnd=wait_for(manager, lambda: window_for(manager.pid), f"WPF window on start {cycle}")
                for identifier, path, key in reversed(original):
                    if key != cold_key:
                        secondary = subprocess.Popen([str(app),"--open-profile",key,"--manager-home",str(home)], cwd=root,
                            env={**os.environ, "MULTI_CLAUDE_HOME":str(root/"unused"), "MULTI_CLAUDE_SMOKE_ROLE":"secondary"})
                        try:
                            if secondary.wait(timeout=20): raise RuntimeError("Shortcut dispatch failed")
                        finally:
                            if secondary.poll() is None: secondary.kill(); secondary.wait()
                    result=wait_for(manager, lambda:set(receipts.glob("*.receipt"))-seen, f"opening profile {identifier} on run {cycle}")
                    if len(result)!=1: raise AssertionError("Duplicate desktop launch")
                    note=result.pop()
                    seen.add(note)
                    code=home/"claude-code"/key
                    expected=["wpf","manager",f"--user-data-dir={home / path}",str(code),str(code/"anthropic")]
                    assert note.read_text(encoding="utf-8-sig").splitlines()==expected
                ctypes.windll.user32.PostMessageW(wintypes.HWND(hwnd),0x10,0,0)
                assert manager.wait(timeout=20)==0
            finally:
                if manager.poll() is None: manager.kill(); manager.wait()
            with contextlib.closing(sqlite3.connect(home/"perfis.db")) as con:
                assert con.execute("SELECT id,pasta,code_key FROM perfis JOIN profile_options ON id=profile ORDER BY id").fetchall()==original
                assert con.execute("SELECT count(*) FROM profile_options WHERE last_open IS NOT NULL").fetchone()==(2,)
                assert con.execute("SELECT count(*) FROM config WHERE chave IN (?, ?, ?)", ("login_pasta","login_ok","login_url")).fetchone()==(0,)
            assert not (root/"unused").exists()
            print(f"PASS published WPF run {cycle}: both shortcuts opened their original Desktop and Code paths.", flush=True)


if __name__ == "__main__":
    main()
