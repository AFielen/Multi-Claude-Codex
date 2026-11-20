"""Open and close the frozen Windows GUI in an isolated, empty portable folder."""
import ctypes
from ctypes import wintypes as wt
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile
import time


def find_window(exe):
    user32, kernel = ctypes.windll.user32, ctypes.windll.kernel32
    kernel.OpenProcess.restype = wt.HANDLE
    found = []
    callback = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    @callback
    def check(hwnd, _):
        title = ctypes.create_unicode_buffer(128)
        user32.GetWindowTextW(wt.HWND(hwnd), title, len(title))
        if title.value != "Multi Claude":
            return True
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(wt.HWND(hwnd), ctypes.byref(pid))
        handle = kernel.OpenProcess(0x1000, False, pid)
        if handle:
            try:
                path = ctypes.create_unicode_buffer(32768)
                size = wt.DWORD(len(path))
                if kernel.QueryFullProcessImageNameW(wt.HANDLE(handle), 0, path, ctypes.byref(size)):
                    if Path(path.value) == exe:
                        found.append(hwnd)
            finally:
                kernel.CloseHandle(wt.HANDLE(handle))
        return not found

    user32.EnumWindows(check, 0)
    return found[0] if found else None


def main():
    source = Path(__file__).resolve().parents[1] / "dist" / "Multi Claude.exe"
    with tempfile.TemporaryDirectory(prefix="multi-claude-smoke-") as scratch:
        folder = Path(scratch)
        exe = folder / source.name
        shutil.copyfile(source, exe)
        database = folder / "perfis.db"
        with sqlite3.connect(database) as conn:
            conn.execute("CREATE TABLE config (chave TEXT PRIMARY KEY, valor TEXT)")
            conn.executemany("INSERT INTO config VALUES (?, ?)", [("idioma", "en"), ("tema", "claro"),
                                                                  ("geometria", "640x680+30000+30000")])
        conn.close()
        proc = subprocess.Popen([str(exe)], cwd=folder, env={**os.environ, "MULTI_CLAUDE_HOME": str(folder)})
        try:
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                if proc.poll() is not None:
                    raise RuntimeError(f"GUI exited before opening (exit {proc.returncode})")
                hwnd = find_window(exe)
                if hwnd:
                    break
                time.sleep(0.2)
            else:
                raise TimeoutError("The packaged GUI did not open")
            if not ctypes.windll.user32.PostMessageW(wt.HWND(hwnd), 0x0010, 0, 0):  # WM_CLOSE
                raise ctypes.WinError()
            if proc.wait(timeout=20) != 0:
                raise RuntimeError(f"GUI closed with exit {proc.returncode}")
        finally:
            if proc.poll() is None:
                taskkill = Path(os.environ["SystemRoot"]) / "System32" / "taskkill.exe"
                subprocess.run([str(taskkill), "/PID", str(proc.pid), "/T", "/F"], check=True,
                               creationflags=subprocess.CREATE_NO_WINDOW, capture_output=True)
                proc.wait(timeout=10)
        with sqlite3.connect(database) as conn:
            count = conn.execute("SELECT count(*) FROM perfis").fetchone()[0]
        conn.close()
        pictures = list((folder / "profile_pictures").glob("*.png"))
        if count or len(pictures) != 10 or "ERRO:" in (folder / "log.txt").read_text(encoding="utf8"):
            raise RuntimeError("Unexpected portable state or GUI callback error")
    print("PASS: frozen GUI opened, installed ten default pictures and closed normally.")


if __name__ == "__main__":
    main()
