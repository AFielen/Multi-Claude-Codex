"""Check frozen EXE shortcut IPC with two migrated profiles and a fictional desktop launcher."""
import contextlib
import ctypes
from ctypes import wintypes as wt
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
from smoke_exe import find_window


RECORDER = """
using System;
using System.IO;

class FixtureDesktop {
    static int Main(string[] args) {
        string output = Environment.GetEnvironmentVariable("MULTI_CLAUDE_SMOKE_RECEIPTS");
        if (!Directory.Exists(output) || args.Length != 1) return 2;
        string temp = Path.Combine(output, Guid.NewGuid().ToString("N") + ".pending");
        File.WriteAllLines(temp, new string[] {
            Environment.GetEnvironmentVariable("MULTI_CLAUDE_SMOKE_RUN"),
            Environment.GetEnvironmentVariable("MULTI_CLAUDE_SMOKE_ROLE"),
            args[0],
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("ANTHROPIC_CONFIG_DIR")
        });
        File.Move(temp, Path.ChangeExtension(temp, ".receipt"));
        return 0;
    }
}
"""


def wait_for(manager, check, description, timeout=40):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if manager.poll() is not None:
            raise RuntimeError(f"Primary packaged manager exited ({manager.returncode}) while waiting for {description}")
        value = check()
        if value:
            return value
        time.sleep(.1)
    raise TimeoutError(f"Timed out waiting for {description}")


@contextlib.contextmanager
def running(args, folder, env):
    process = subprocess.Popen(args, cwd=folder, env=env)
    try:
        yield process
    finally:
        if process.poll() is None:
            # Only this test's specific PyInstaller bootloader and its descendants.
            taskkill = Path(os.environ["SystemRoot"], "System32/taskkill.exe")
            subprocess.run([str(taskkill), "/PID", str(process.pid), "/T", "/F"],
                           capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
            process.wait(timeout=15)


def catalog_query(path, sql, parameters=()):
    with contextlib.closing(sqlite3.connect(path)) as con, con:
        return con.execute(sql, parameters).fetchall()


@contextlib.contextmanager
def temporary_fixture():
    scratch = tempfile.TemporaryDirectory(prefix="multi-claude-ipc-")
    try:
        yield Path(scratch.name)
    finally:
        # Windows can briefly retain the executable mapping after a process tree exits.
        deadline = time.monotonic() + 10
        while True:
            try:
                scratch.cleanup()
                break
            except PermissionError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(.2)


def main():
    build = Path(__file__).resolve().parents[1] / "dist/Multi Claude.exe"
    system = Path(os.environ["SystemRoot"])
    compiler = next((p for arch in ("Framework64", "Framework")
                     if (p := system / "Microsoft.NET" / arch / "v4.0.30319/csc.exe").is_file()), None)
    if compiler is None:
        raise SystemExit("The .NET Framework C# compiler is required for the desktop fixture")
    with temporary_fixture() as root:
        app = root / "Multi Claude.exe"
        shutil.copyfile(build, app)
        home = root / "portable home"
        receipts = root / "receipts"
        user = root / "fictional user"
        local = user / "AppData/Local"
        roaming = user / "AppData/Roaming"
        launcher = local / "AnthropicClaude/claude.exe"
        for path in (home, receipts, roaming, launcher.parent):
            path.mkdir(parents=True)
        source = root / "FixtureDesktop.cs"
        source.write_text(RECORDER, encoding="ascii")
        subprocess.run([str(compiler), "/nologo", "/target:winexe", f"/out:{launcher}", str(source)],
                       cwd=root, check=True, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
        names = ("Fixture Amber", "Fixture Blue")
        catalog = home / "perfis.db"
        with contextlib.closing(sqlite3.connect(catalog)) as con, con:
            con.execute("CREATE TABLE perfis(id INTEGER PRIMARY KEY, nome TEXT UNIQUE NOT NULL, "
                        "pasta TEXT NOT NULL, criado TEXT DEFAULT CURRENT_TIMESTAMP)")
            con.executemany("INSERT INTO perfis(nome,pasta) VALUES (?,?)", [(n, str(Path("dados", n))) for n in names])
            con.execute("CREATE TABLE config(chave TEXT PRIMARY KEY,valor TEXT)")
            con.executemany("INSERT INTO config VALUES (?,?)",
                            [("idioma","en"), ("tema","claro"), ("geometria","860x680+30000+30000")])
        run_id = uuid.uuid4().hex
        env = {**os.environ, "USERPROFILE": str(user), "HOME": str(user),
               "APPDATA": str(roaming), "LOCALAPPDATA": str(local),
               "MULTI_CLAUDE_HOME": str(home), "MULTI_CLAUDE_SMOKE_RUN": run_id,
               "MULTI_CLAUDE_SMOKE_RECEIPTS": str(receipts), "MULTI_CLAUDE_SMOKE_ROLE": "primary"}
        with running([str(app)], root, env) as manager:
            hwnd = wait_for(manager, lambda: find_window(app), "primary test window")
            wait_for(manager, lambda: catalog_query(catalog,
                     "SELECT 1 FROM sqlite_master WHERE type='table' AND name='profile_options'"),
                     "committed catalog migration")
            rows = catalog_query(catalog, "SELECT nome,code_key,isolated,shortcut FROM perfis JOIN profile_options ON profile=id")
            if ({r[0] for r in rows} != set(names) or len({r[1] for r in rows}) != 2
                    or any(not re.fullmatch("[0-9a-f]{32}", key) or isolated or shortcut for _, key, isolated, shortcut in rows)):
                raise AssertionError("Two legacy profiles were not safely migrated")
            keys = {name:key for name,key,_,_ in rows}
            catalog_query(catalog, "UPDATE profile_options SET isolated=1")
            catalog_query(catalog, "CREATE TABLE IF NOT EXISTS open_requests(code_key TEXT PRIMARY KEY, requested REAL)")
            catalog_query(catalog, "INSERT INTO open_requests VALUES (?,?)", (keys[names[0]],time.time()-3600))
            wait_for(manager, lambda: not catalog_query(catalog,"SELECT 1 FROM open_requests"), "expired IPC request consumption")
            if list(receipts.glob("*.receipt")):
                raise AssertionError("Expired activation launched a profile")

            sender_env = {**env, "MULTI_CLAUDE_SMOKE_ROLE": "secondary",
                          "MULTI_CLAUDE_HOME": str(root / "unused secondary home")}
            seen = set()
            for name in reversed(names):
                key = keys[name]
                with running([str(app), "--open-profile", key, "--manager-home", str(home)], root, sender_env) as sender:
                    if sender.wait(timeout=30) != 0:
                        raise RuntimeError(f"Shortcut process exited {sender.returncode}")
                new = wait_for(manager, lambda: set(receipts.glob("*.receipt"))-seen, "fixture launch receipt")
                if len(new) != 1:
                    raise AssertionError("A profile activation started multiple launchers")
                note = new.pop()
                seen.add(note)
                config = home / "claude-code" / key
                actual = note.read_text(encoding="utf-8-sig").splitlines()
                expected = [run_id, "primary", f"--user-data-dir={home / 'dados' / name}", str(config), str(config / "anthropic")]
                if actual != expected or not config.is_dir():
                    raise AssertionError(f"Profile IPC selected the wrong launcher, desktop path or code identity: {name}")
                wait_for(manager, lambda: catalog_query(catalog,
                         "SELECT 1 FROM profile_options WHERE code_key=? AND last_open IS NOT NULL", (key,)),
                         "persisted activation time")
            if catalog_query(catalog, "SELECT 1 FROM open_requests") or (root/"unused secondary home").exists():
                raise AssertionError("Shortcut did not exclusively use the primary catalog")
            if not ctypes.windll.user32.PostMessageW(wt.HWND(hwnd), 0x0010, 0, 0):
                raise ctypes.WinError()
            if manager.wait(timeout=20) != 0:
                raise RuntimeError("Primary packaged manager did not close cleanly")
        if "ERRO:" in (home / "log.txt").read_text(encoding="utf8") or set(receipts.glob("*.receipt")) != seen:
            raise AssertionError("Unexpected GUI error or duplicate desktop launch")
    print("PASS: frozen EXE migrated two legacy profiles, discarded an expired request and routed "
          "two shortcut invocations through the primary GUI timer to their separate desktop and Code paths.")


if __name__ == "__main__":
    main()
