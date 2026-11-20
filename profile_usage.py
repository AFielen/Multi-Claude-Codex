"""Local Claude Code counters for a single, explicitly bound portable profile.

Source fields: https://code.claude.com/docs/en/statusline#available-data and
https://code.claude.com/docs/en/agent-sdk/cost-tracking#track-per-step-usage .
Recorded transcript counts describe retained Claude Code sessions only.
"""
import base64
from dataclasses import dataclass
from datetime import datetime
from functools import lru_cache
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import time

from profile_code import code_config_dir


_FIELDS = ("input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens")
_WINDOWS = ("five_hour", "seven_day")
_OPT_IN = "multiclaude-limits.enabled"
_SNAPSHOT = "multiclaude-limits.json"
_LINE_LIMIT = 8 * 1024 * 1024


@dataclass(frozen=True)
class TokenUsage:
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read_tokens: int = 0
    cache_write_tokens: int = 0
    requests: int = 0
    available: bool = False
    incomplete: bool = False

    @property
    def total_tokens(self):
        return self.input_tokens + self.output_tokens + self.cache_read_tokens + self.cache_write_tokens


def _plain(path):
    """Reject redirected ancestors, special files and hard-linked regular files."""
    for entry in (*reversed(path.parents), path):
        try:
            st = entry.lstat()
        except FileNotFoundError:
            continue
        if (stat.S_ISLNK(st.st_mode)
                or getattr(st, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            return False
        if entry != path and not stat.S_ISDIR(st.st_mode):
            return False
        if entry == path and not (stat.S_ISDIR(st.st_mode) or
                                   (stat.S_ISREG(st.st_mode) and st.st_nlink == 1)):
            return False
    return True


def _root(directory):
    root = Path(directory)
    if root != code_config_dir(root.parent.parent, root.name) or not _plain(root):
        raise ValueError("Expected a dedicated Claude Code profile directory")
    return root


def _json_object(path, limit):
    if not _plain(path):
        raise ValueError("Redirected profile file")
    with path.open("rb") as stream:
        raw = stream.read(limit + 1)
    if len(raw) > limit:
        raise ValueError("Profile metadata is too large")
    def unique(pairs):
        entries = dict(pairs)
        if len(entries) != len(pairs):
            raise ValueError("Ambiguous duplicate JSON field")
        return entries

    obj = json.loads(raw, object_pairs_hook=unique)
    if not isinstance(obj, dict):
        raise ValueError("Invalid profile metadata")
    return raw, obj


def _request(row):
    if not isinstance(row, dict):
        raise ValueError
    if row.get("type") != "assistant":
        return None
    message = row.get("message")
    if not isinstance(message, dict):
        raise ValueError
    counters = message.get("usage")
    if counters is None or message.get("model") == "<synthetic>":
        return None
    key = message.get("id")
    if not isinstance(key, str) or not key or len(key) > 256 or not isinstance(counters, dict):
        raise ValueError
    if not any(k in counters for k in _FIELDS):
        raise ValueError
    values = tuple(counters.get(k, 0) for k in _FIELDS)
    if not all(type(v) is int and 0 <= v <= 2**63-1 for v in values):
        raise ValueError
    timestamp = row.get("timestamp")
    stamp = None
    if isinstance(timestamp, str) and len(timestamp) <= 64:
        try:
            dt = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
            if dt.tzinfo:
                stamp = dt.timestamp()
        except (ValueError, OSError, OverflowError):
            pass
    # Individual assistant events can have the same id while usage is still streaming.
    complete = bool(message.get("stop_reason"))
    return key, (complete, values[1], values, stamp)


def _merge(result, item):
    key, sample = item
    previous = result.get(key)
    # A finished response wins over an incremental report. Within a phase keep
    # the fullest output snapshot, never add repeated input/cache charges.
    if previous is None or sample[:3] > previous[:3]:
        result[key] = sample


@lru_cache(maxsize=128)
def _transcript(path, identity):
    items, incomplete = {}, False
    with path.open("rb") as stream:
        actual = os.fstat(stream.fileno())
        if (actual.st_dev, actual.st_ino) != identity[:2] or actual.st_nlink != 1:
            raise OSError("Transcript changed during discovery")
        while line := stream.readline(_LINE_LIMIT + 1):
            if len(line) > _LINE_LIMIT:
                while line and not line.endswith(b"\n"):
                    line = stream.readline(_LINE_LIMIT)
                incomplete = True
                continue
            try:
                request = _request(json.loads(line))
                if request:
                    _merge(items, request)
            except (ValueError, RecursionError, UnicodeError):
                incomplete = True
    return items, incomplete


def read_usage(directory, since=None):
    """Aggregate retained per-response counters. since is a Unix timestamp."""
    root = _root(directory)
    projects = root / "projects"
    if not _plain(projects):
        return TokenUsage(incomplete=True)
    if not projects.is_dir():
        return TokenUsage()
    requests = {}
    partial = False

    def failed(_):
        nonlocal partial
        partial = True

    for parent, dirs, files in os.walk(projects, onerror=failed):
        parent = Path(parent)
        parts = parent.relative_to(projects).parts
        if len(parts) == 1:
            dirs[:] = [d for d in dirs if d != "memory"]
        if len(parts) == 2:
            dirs[:] = [d for d in dirs if d == "subagents"]
        allowed = []
        for name in dirs:
            try:
                entry = (parent / name).lstat()
                if stat.S_ISDIR(entry.st_mode) and not (entry.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT):
                    allowed.append(name)
            except OSError:
                partial = True
        dirs[:] = allowed
        if len(parts) != 1 and not (len(parts) >= 3 and parts[2] == "subagents"):
            continue
        for name in files:
            if not name.endswith(".jsonl"):
                continue
            path = parent / name
            try:
                st = path.lstat()
                if (not stat.S_ISREG(st.st_mode) or st.st_nlink != 1
                        or st.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT):
                    partial = True
                    continue
                rows, bad = _transcript(path, (st.st_dev, st.st_ino, st.st_mtime_ns, st.st_size))
                partial |= bad
                for sample in rows.items():
                    _merge(requests, sample)
            except OSError:
                partial = True

    totals, count = [0] * 4, 0
    for complete, _, values, stamp in requests.values():
        if since is not None and (stamp is None or stamp < since):
            partial |= stamp is None
            continue
        count += 1
        partial |= not complete
        for i, number in enumerate(values):
            totals[i] += number
    return TokenUsage(*totals, count, bool(requests), partial)


# Windows PowerShell and Git Bash both pass a single base64 argument unchanged.
# This is PowerShell's UTF-16LE command transport. The script is fixed except
# for the checked profile UUID. No file paths or transcript data become code.
_STATUS = r"""
$ErrorActionPreference = 'Stop'
try {
    $d = [Environment]::GetEnvironmentVariable('CLAUDE_CONFIG_DIR')
    if ($d -notmatch '^[a-zA-Z]:[\\/]') { return }
    $dir = [IO.DirectoryInfo]::new($d)
    if ($dir.Name -cne '__UUID__' -or $dir.Parent.Name -ne 'claude-code') { return }
    for ($p=$dir; $null -ne $p; $p=$p.Parent) {
        if ($p.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
    }
    $flag = Join-Path $d 'multiclaude-limits.enabled'
    if (-not [IO.File]::Exists($flag) -or ([IO.File]::GetAttributes($flag) -band 1024)) { return }
    if ([IO.FileInfo]::new($flag).Length -ne 97) { return }
    if ([IO.File]::ReadAllText($flag) -cnotmatch '^__UUID__:[a-f0-9]{64}$') { return }
    $json = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $result = @{observed_at=$now}
    $labels = @()
    foreach ($name in 'five_hour','seven_day') {
        $w = $json.rate_limits.$name
        if ($null -eq $w) { continue }
        $v = $w.used_percentage
        $r = $w.resets_at
        if (($v -isnot [double] -and $v -isnot [long] -and $v -isnot [int] -and $v -isnot [decimal]) -or
            ($r -isnot [long] -and $r -isnot [int]) -or $v -lt 0 -or $v -gt 100 -or $r -le $now -or $r -gt 253402300799) { continue }
        $result[$name] = @{used_percentage=$v; resets_at=$r}
        $label = if ($name -eq 'five_hour') { '5h' } else { '7d' }
        $labels += ($label + ' ' + $v + '%')
    }
    if ($labels.Count -eq 0) { return }
    $target = Join-Path $d 'multiclaude-limits.json'
    $tmp = Join-Path $d ([Guid]::NewGuid().ToString('N') + '.rate.tmp')
    try {
        [IO.File]::WriteAllText($tmp, ($result | ConvertTo-Json -Compress))
        if (-not [IO.File]::Exists($flag)) { return }
        if ([IO.File]::Exists($target)) {
            if ([IO.File]::GetAttributes($target) -band 1024) { return }
            [IO.File]::Replace($tmp,$target,$tmp+'.old')
        } else { [IO.File]::Move($tmp,$target) }
    } finally {
        [IO.File]::Delete($tmp)
        [IO.File]::Delete($tmp+'.old')
    }
    [Console]::WriteLine(($labels -join ' | '))
} catch { }
"""


@lru_cache(maxsize=64)
def _status_command(uid):
    import ctypes
    buf = ctypes.create_unicode_buffer(32768)
    if not ctypes.windll.kernel32.GetSystemDirectoryW(buf, len(buf)):
        raise OSError("Windows system directory unavailable")
    shell = Path(buf.value, "WindowsPowerShell/v1.0/powershell.exe")
    if not ctypes.windll.kernel32.GetShortPathNameW(str(shell), buf, len(buf)):
        raise OSError("Windows PowerShell unavailable")
    program = Path(buf.value).as_posix()
    if not re.fullmatch(r"[a-zA-Z]:/[a-zA-Z0-9_./~-]+", program):
        raise ValueError("The Windows PowerShell path cannot be passed to both supported shells")
    encoded = base64.b64encode(_STATUS.replace("__UUID__", uid).encode("utf-16-le")).decode("ascii")
    return program + " -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded


def _status_config(root):
    return {"type": "command", "command": _status_command(root.name)}


def _atomic(path, data, *, expected=None, compare=False):
    if not _plain(path):
        raise ValueError("Redirected profile file")
    fd, name = tempfile.mkstemp(dir=path.parent, prefix=".multiclaude-", suffix=".tmp")
    tmp = Path(name)
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        if compare and expected is None:
            # Windows rename refuses an existing name, including on FAT removable drives.
            if os.name == "nt":
                tmp.rename(path)
            else:
                os.link(tmp, path)
        else:
            if compare and _json_object(path, 2**20)[0] != expected:
                raise ValueError("Settings changed while editing")
            tmp.replace(path)
    finally:
        tmp.unlink(missing_ok=True)


def _command_digest(config):
    if not isinstance(config, dict) or set(config) != {"command", "type"} or config["type"] != "command":
        return None
    if not isinstance(config["command"], str):
        return None
    return hashlib.sha256(config["command"].encode("utf8")).hexdigest()


def _installed_digest(root):
    flag = root / _OPT_IN
    if _plain(flag) and flag.is_file() and flag.stat().st_size == 97:
        match = re.fullmatch(root.name.encode() + b":([a-f0-9]{64})", flag.read_bytes())
        if match:
            return match[1].decode()
    return None


def limit_capture_enabled(directory):
    root = _root(directory)
    try:
        installed = _installed_digest(root)
        if not installed:
            return False
        return _command_digest(_json_object(root / "settings.json", 2**20)[1].get("statusLine")) == installed
    except (OSError, ValueError, RecursionError):
        return False


def set_limit_capture(directory, enabled):
    """Returns False if a foreign status line or unreadable settings prevent the change."""
    root = _root(directory)
    if not root.is_dir() and not enabled:
        return True
    root.mkdir(parents=True, exist_ok=True)
    settings = root / "settings.json"
    flag = root / _OPT_IN
    target = root / _SNAPSHOT
    own = _status_config(root)
    marker = (root.name + ":" + _command_digest(own)).encode()
    lock = root / ".multiclaude-settings.lock"
    if not all(_plain(p) for p in (flag, target, settings, lock)):
        return False
    lease = lock.open("a+b")
    try:
        # The OS releases the lock on process exit, including an interrupted save.
        if os.name == "nt":
            import msvcrt
            lease.seek(0)
            msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            original, obj = _json_object(settings, 2**20)
        except FileNotFoundError:
            original, obj = None, {}
        installed = _installed_digest(root)
        if flag.exists() and not installed:
            return False
        if not enabled:
            if installed:
                flag.unlink()
                target.unlink(missing_ok=True)
        if ("statusLine" in obj and obj["statusLine"] != own and
                (not installed or _command_digest(obj["statusLine"]) != installed)):
            return False
        if enabled and obj.get("statusLine") != own:
            obj["statusLine"] = own
        elif not enabled and "statusLine" in obj:
            del obj["statusLine"]
        else:
            if enabled and installed != _command_digest(own):
                _atomic(flag, marker)
            return True
        payload = (json.dumps(obj, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        _atomic(settings, payload, expected=original, compare=True)
        if enabled:
            _atomic(flag, marker)
        return True
    except (OSError, ValueError, RecursionError):
        return False
    finally:
        lease.close()


def read_limits(directory, now=None):
    """Return only unexpired observed windows. No data means an empty mapping."""
    root = _root(directory)
    now = time.time() if now is None else now
    try:
        if not _installed_digest(root):
            return {}
        _, data = _json_object(root / _SNAPSHOT, 4096)
    except (OSError, ValueError, RecursionError):
        return {}
    observed = data.get("observed_at")
    if type(observed) is not int or not 0 < observed <= now + 2:
        return {}
    result = {}
    for name in _WINDOWS:
        w = data.get(name)
        if not isinstance(w, dict):
            continue
        used, resets = w.get("used_percentage"), w.get("resets_at")
        if (type(used) in (int, float) and 0 <= used <= 100
                and type(resets) is int and max(now, observed) < resets <= 253402300799):
            result[name] = {"used_percentage": used, "resets_at": resets}
    return {"observed_at": observed, **result} if result else {}
