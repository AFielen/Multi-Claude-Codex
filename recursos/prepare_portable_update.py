"""Stage a Windows release and check a same-volume Explorer move of a closed portable profile store."""
import argparse
import base64
import contextlib
import ctypes
from ctypes import wintypes
import hashlib
import os
from pathlib import Path
import re
import shutil
import sqlite3
import stat
import subprocess
import sys
import tempfile
import _winapi


PACKAGE = ("Multi Claude.exe", "LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.txt")
LAUNCHER = "Start Multi Claude.cmd"
BRIDGE = "Start Multi Claude - Existing Profiles.cmd"
_START = r"""@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "MULTI_CLAUDE_HOME=%~dp0Profiles"
if not exist "%MULTI_CLAUDE_HOME%\perfis.db" goto missing
if not exist "%~dp0App\Multi Claude.exe" goto missing
start "" "%~dp0App\Multi Claude.exe"
exit /b %errorlevel%
:missing
echo Expected App\Multi Claude.exe and Profiles\perfis.db next to this launcher.
pause
exit /b 1
"""
_LAUNCHER_BYTES = _START.replace("\n", "\r\n").encode("ascii")


def _plain(value):
    path = Path(value)
    if (not path.is_absolute() or not re.fullmatch("[A-Za-z]:", path.drive) or ".." in path.parts
            or any(os.path.isreserved(p) for p in path.parts[1:])):
        raise ValueError("Use an absolute local Windows path.")
    if ctypes.windll.kernel32.GetDriveTypeW(str(path.anchor)) not in (2, 3, 6):
        raise ValueError("Use a local fixed or removable drive.")
    for part in (*reversed(path.parents), path):
        try:
            info = part.lstat()
        except FileNotFoundError:
            break
        if info.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError(f"Redirected path: {part}")
    return path


def _temporary_roots():
    return [Path(p) for p in (os.environ.get("TEMP"), os.environ.get("TMP"), tempfile.gettempdir()) if p]


def _durable(destination):
    destination = _plain(destination).resolve()
    if any(destination.is_relative_to(root.resolve()) for root in _temporary_roots()):
        raise ValueError("The destination must be outside Windows temporary directories.")
    return destination


def _digest(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def _stable_external(path, source, temporary):
    # Expand short DOS names. Reject redirected/remote ancestors before following the path.
    resolved = _plain(path).resolve(strict=True)
    return not any(resolved.is_relative_to(root) for root in (source, *temporary))


def stage(package, destination, expected_sha256=None):
    """Stage the release files and launcher, verifying each copied file."""
    package, destination = _plain(package).resolve(strict=True), _durable(destination)
    if package.is_relative_to(destination) or destination.is_relative_to(package):
        raise ValueError("Use separate package and destination folders.")
    files = [_plain(package / name) for name in PACKAGE]
    if not all(path.is_file() for path in files):
        raise ValueError("The package needs Multi Claude.exe, LICENSE, NOTICE and THIRD-PARTY-NOTICES.txt.")
    # Older releases predate the separate credits file.
    if os.path.lexists(package / "AUTHORS"):
        credits = _plain(package / "AUTHORS")
        if not credits.is_file():
            raise ValueError("AUTHORS must be a regular file.")
        files.append(credits)
    hashes = {path.name: _digest(path) for path in files}
    if expected_sha256 and hashes[PACKAGE[0]] != expected_sha256.lower():
        raise ValueError("The executable does not match the supplied SHA-256.")
    destination.mkdir(parents=True, exist_ok=True)
    if any(destination.iterdir()):
        raise ValueError("The destination must be empty.")
    with tempfile.TemporaryDirectory(dir=destination, prefix=".release-") as tmp:
        pending = Path(tmp)
        app = pending / "App"
        app.mkdir()
        for path in files:
            target = app / path.name
            shutil.copyfile(path, target)
            if _digest(target) != hashes[path.name]:
                raise OSError(f"Copy verification failed: {path.name}")
        starter = pending / LAUNCHER
        starter.write_bytes(_LAUNCHER_BYTES)
        launcher = destination / LAUNCHER
        starter_info = starter.stat()
        starter.rename(launcher)
        try:
            app.rename(destination / "App")
        except OSError:
            if (os.path.lexists(launcher) and os.path.samestat(starter_info, launcher.lstat())
                    and launcher.read_bytes() == _LAUNCHER_BYTES):
                launcher.unlink()
            raise
    return hashes[PACKAGE[0]]


def _require_idle(source):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenMutexW.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR)
    kernel.OpenMutexW.restype = wintypes.HANDLE
    # Keep this standalone helper compatible with windows_profiles.manager_mutex_name.
    normalized = os.path.normcase(os.path.realpath(source))
    mutex = "MultiClaude_" + hashlib.md5(normalized.encode()).hexdigest()[:16]
    handle = kernel.OpenMutexW(0x00100000, False, mutex)
    if handle:
        kernel.CloseHandle(wintypes.HANDLE(handle))
        raise RuntimeError("The manager using this profile folder is still running.")
    if ctypes.get_last_error() != 2:
        raise RuntimeError("Could not verify whether the manager is closed.")


def _probe_access(path):
    # OPEN_EXISTING, zero sharing, OPEN_REPARSE_POINT and BACKUP_SEMANTICS for directories.
    # Never follow the leaf link or read any bytes. Release this entry before inspecting the next.
    try:
        handle = _winapi.CreateFile(str(path), _winapi.GENERIC_READ, 0, 0, _winapi.OPEN_EXISTING,
                                    0x00200000 | 0x02000000, 0)
    except OSError as exc:
        if exc.winerror in (32, 33):
            raise RuntimeError(f"A handle is in use in the selected profile folder: {path}") from exc
        raise
    _winapi.CloseHandle(handle)


def _check_catalog(source):
    catalog = _plain(source / "perfis.db")
    if not catalog.is_file():
        raise ValueError("The selected folder does not contain a profile catalog.")
    for suffix in ("-wal", "-journal"):
        sidecar = _plain(source / ("perfis.db" + suffix))
        if sidecar.exists() and sidecar.stat().st_size:
            raise ValueError("SQLite recovery or checkpointing may be pending. Keep the complete source folder.")
    # immutable prevents SQLite from creating -shm or locking files during this read-only check.
    with contextlib.closing(sqlite3.connect(catalog.as_uri() + "?mode=ro&immutable=1", uri=True)) as db:
        if db.execute("PRAGMA quick_check").fetchall() != [("ok",)]:
            raise ValueError("SQLite catalog integrity check failed.")
        temporary = [root.resolve() for root in _temporary_roots()]
        original = source.resolve(strict=True)
        for (raw,) in db.execute("SELECT pasta FROM perfis"):
            if not isinstance(raw, str) or not raw or "\0" in raw:
                raise ValueError("The catalog contains an invalid profile location.")
            path = Path(raw)
            if any(os.path.isreserved(p) for p in (path.parts[1:] if path.anchor else path.parts)):
                raise ValueError("The catalog contains an invalid Windows location.")
            if path.is_absolute():
                path = Path(os.path.normpath(path))
                if not re.fullmatch("[A-Za-z]:", path.drive):
                    raise ValueError("The catalog contains a non-local absolute profile location.")
                if not _stable_external(path, original, temporary):
                    raise ValueError("An absolute catalog path still points into the source or a temporary folder.")
            elif path.drive or path.root or ".." in path.parts:
                raise ValueError("A relative catalog location would change its meaning after moving.")


def _check_links(source):
    temporary = [root.resolve() for root in _temporary_roots()]
    original = source.resolve(strict=True)
    folders = [source]
    _probe_access(source)
    while folders:
        with os.scandir(folders.pop()) as entries:
            for entry in entries:
                info = entry.stat(follow_symlinks=False)
                _probe_access(entry.path)
                if info.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    if info.st_reparse_tag not in (stat.IO_REPARSE_TAG_SYMLINK, stat.IO_REPARSE_TAG_MOUNT_POINT):
                        raise ValueError("Unsupported reparse point in the profile store.")
                    link = Path(entry.path)
                    raw = str(link.readlink())
                    if raw.startswith("\\\\?\\"):
                        raw = raw[4:]
                        if not re.match(r"^[A-Za-z]:[\\/]", raw):
                            raise ValueError("Link target uses an unsupported Windows namespace.")
                    target = Path(os.path.normpath(raw))
                    if target.is_absolute():
                        if not _stable_external(target, original, temporary):
                            raise ValueError("Link target would be unavailable after moving.")
                    elif target.drive or target.root or not Path(os.path.normpath(link.parent / target)).is_relative_to(source):
                        raise ValueError("Relative Link target would change after moving.")
                elif stat.S_ISDIR(info.st_mode):
                    folders.append(entry.path)


def _staged(destination):
    starter = _plain(destination / LAUNCHER)
    if (not starter.is_file() or starter.stat().st_size != len(_LAUNCHER_BYTES)
            or starter.read_bytes() != _LAUNCHER_BYTES
            or not all(_plain(destination / "App" / n).is_file() for n in PACKAGE)):
        raise ValueError("Stage the new release in the destination first.")
    return destination / "App" / PACKAGE[0]


def bridge(source, destination, expected_sha256):
    """Create a one-click launcher for the original catalog, without opening the manager."""
    source = _plain(source).resolve(strict=True)
    destination = _durable(destination)
    if source.is_relative_to(destination) or destination.is_relative_to(source):
        raise ValueError("Use a staged package outside the original profile folder.")
    executable = _staged(destination)
    if not re.fullmatch(r"[a-fA-F0-9]{64}", expected_sha256) or _digest(executable) != expected_sha256.lower():
        raise ValueError("The executable does not match the supplied SHA-256.")
    catalog = _plain(source / "perfis.db")
    identity = catalog.stat()
    if not stat.S_ISREG(identity.st_mode) or not identity.st_ino:
        raise ValueError("The original catalog must have a stable local file ID.")
    # Schema only. Do not inspect profile rows, change journal mode, or create SQLite sidecars.
    with contextlib.closing(sqlite3.connect(catalog.as_uri() + "?mode=ro&immutable=1", uri=True)) as db:
        columns = {r[1] for r in db.execute("PRAGMA table_info(perfis)")}
    if not {"id", "nome", "pasta"} <= columns or not os.path.samestat(catalog.stat(), identity):
        raise ValueError("The selected file is not the expected profile catalog.")
    system32 = Path(os.environ["SystemRoot"]) / "System32"
    mount = subprocess.run([str(system32 / "mountvol.exe"), source.anchor, "/L"], capture_output=True, timeout=10)
    volume = re.search(rb"Volume\{([a-fA-F0-9-]{36})\}", mount.stdout, re.I)
    query = subprocess.run([str(system32 / "fsutil.exe"), "file", "queryfileid", str(catalog)],
                           capture_output=True, timeout=10)
    file_id = f"{identity.st_ino:032x}"
    if (mount.returncode or not volume or query.returncode
            or not query.stdout.strip().lower().endswith(("0x" + file_id).encode("ascii"))):
        raise ValueError("Windows could not confirm the volume and file identity of the original catalog.")
    encoded = base64.b64encode(str(source).encode("utf-8")).decode("ascii")
    if len(encoded) > 7500:
        raise ValueError("The path is too long for a Windows command launcher.")
    # Only fixed code and validated hex enter PowerShell. Keep Unicode paths as data.
    script = """
    $ErrorActionPreference='Stop';
    try {
        $base=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:MC_ORIGINAL_BASE));
        $db=[IO.Path]::Combine($base,'perfis.db');
        $exe=$env:MC_RELEASE_EXE;
        foreach($item in @($exe,$db)) {
            for($p=$item;$p;$p=[IO.Path]::GetDirectoryName($p)) {
                if ([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) {throw 'A profile or release path was redirected.'}
            }
        }
        $catalog=[IO.File]::Open($db,'Open','Read','ReadWrite');
        $volume=& ([IO.Path]::Combine([Environment]::SystemDirectory,'mountvol.exe')) ([IO.Path]::GetPathRoot($db)) /L;
        if ($LASTEXITCODE -ne 0 -or $volume -notmatch 'EXPECTED_VOLUME') {throw 'The original profile volume is unavailable.'}
        $id=& ([IO.Path]::Combine([Environment]::SystemDirectory,'fsutil.exe')) file queryfileid $db;
        if ($LASTEXITCODE -ne 0 -or $id -notmatch '0xEXPECTED_ID\\s*$') {throw 'The original profile catalog is missing or has been replaced.'}
        $release=[IO.File]::OpenRead($exe);
        $algorithm=[Security.Cryptography.SHA256]::Create();
        $hash=[BitConverter]::ToString($algorithm.ComputeHash($release)).Replace('-','');
        if ($hash -ne 'EXPECTED_HASH') {throw 'The release executable has changed.'}
        $env:MULTI_CLAUDE_HOME=$base;
        $info=New-Object System.Diagnostics.ProcessStartInfo;
        $info.FileName=$exe; $info.WorkingDirectory=[IO.Path]::GetDirectoryName($exe); $info.UseShellExecute=$false;
        $null=[Diagnostics.Process]::Start($info);
    } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
    finally { if ($algorithm) {$algorithm.Dispose()} if ($release) {$release.Dispose()} if ($catalog) {$catalog.Dispose()} }
    """
    script = (" ".join(script.split()).replace("EXPECTED_ID", file_id)
              .replace("EXPECTED_VOLUME", volume[1].decode("ascii")).replace("EXPECTED_HASH", expected_sha256))
    content = "\r\n".join((
        "@echo off", "setlocal EnableExtensions DisableDelayedExpansion",
        f'set "MC_ORIGINAL_BASE={encoded}"', 'set "MC_RELEASE_EXE=%~dp0App\\Multi Claude.exe"',
        '"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -NonInteractive -Command "' + script + '"',
        "if not errorlevel 1 exit /b 0", "pause", "exit /b 1", "",
    )).encode("ascii")
    starter = destination / BRIDGE
    if os.path.lexists(starter):
        if _plain(starter).is_file() and starter.read_bytes() == content:
            return starter
        raise ValueError("A different existing-profile launcher already exists in the destination.")
    with tempfile.TemporaryDirectory(dir=destination) as tmp:
        pending = Path(tmp) / BRIDGE
        pending.write_bytes(content)
        pending.rename(starter)
    return starter


def check(source, destination):
    """Read-only snapshot. The operator performs a same-volume directory move while apps remain closed."""
    source, destination = _plain(source).resolve(strict=True), _durable(destination)
    home = Path(os.environ["USERPROFILE"])
    protected = {home, home / "Downloads", home / "Desktop", home / "Documents", *_temporary_roots()}
    if (any(path.is_relative_to(source) for path in protected) or source == Path(source.anchor)
            or destination.is_relative_to(source) or source.is_relative_to(destination)):
        raise ValueError("Choose a dedicated manager folder and a separate permanent destination.")
    if source.drive.lower() != destination.drive.lower() or source.stat().st_dev != destination.stat().st_dev:
        raise ValueError("Move within the same volume to keep file identities, junctions and SQLite files together.")
    _staged(destination)
    target = destination / "Profiles"
    if os.path.lexists(target):
        raise ValueError("Profiles already exists in the destination. Do not merge two stores.")
    if os.path.lexists(destination / source.name):
        raise ValueError("A folder or file in the destination has the same name. Do not merge or replace it.")
    _require_idle(source)
    _check_catalog(source)
    _check_links(source)
    _require_idle(source)
    return target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    prepare = commands.add_parser("stage", help="prepare the new executable in an empty permanent directory")
    prepare.add_argument("--package", required=True, type=Path)
    prepare.add_argument("--destination", required=True, type=Path)
    prepare.add_argument("--sha256", help="optional expected SHA-256 of the release executable")
    inspect = commands.add_parser("check", help="check the closed portable store before moving it in Explorer")
    inspect.add_argument("--source", required=True, type=Path)
    inspect.add_argument("--destination", required=True, type=Path)
    existing = commands.add_parser("bridge", help="prepare a starter for the original profile folder before moving")
    existing.add_argument("--source", required=True, type=Path)
    existing.add_argument("--destination", required=True, type=Path)
    existing.add_argument("--sha256", required=True, help="expected SHA-256 of the staged executable")
    args = parser.parse_args()
    try:
        if args.action == "stage":
            digest = stage(args.package, args.destination, args.sha256)
            print(f"Prepared {args.destination / LAUNCHER}\nVerified copy of Multi Claude.exe (SHA-256 {digest}).")
        elif args.action == "bridge":
            starter = bridge(args.source, args.destination, args.sha256)
            print(f"Prepared {starter}. It will use the original profile catalog in {args.source}.")
            print("Close the old manager before opening the starter. The profiles continue to reside in their original folder.")
        else:
            print("Before checking, close the manager, Desktop profiles and Code sessions using this source, including idle terminals.")
            target = check(args.source, args.destination)
            print("No conflicting handle was observed during this read-only check.")
            print("Keep these applications closed until the move completes. A session can open a file after this check.")
            print(f"In Explorer, cut the complete folder {args.source} and paste it inside {args.destination}.")
            print(f"Rename the pasted folder to Profiles so its full path is {target}. Then open {args.destination / LAUNCHER}.")
            print("Recreate exported profile shortcuts and copy fresh terminal commands from the updated manager.")
    except (ValueError, RuntimeError, OSError, sqlite3.Error, subprocess.TimeoutExpired) as exc:
        parser.exit(1, f"Stopped: {exc}\n")


if __name__ == "__main__":
    if sys.platform != "win32":
        raise SystemExit("This preparation tool requires Windows.")
    main()
