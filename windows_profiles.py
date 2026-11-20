"""Windows integration for registered profile processes and explicit global shortcuts."""
import ctypes
from ctypes import wintypes as wt
from enum import Enum
from functools import cache
import hashlib
import os
import re
import tkinter as tk


class FocusResult(Enum):
    MISSING = "missing"
    FOCUSED = "focused"
    BLOCKED = "blocked"


_ENUM_PROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
_SUBCLASS_PROC = ctypes.WINFUNCTYPE(
    ctypes.c_ssize_t, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM, ctypes.c_size_t, ctypes.c_size_t
)
_WM_HOTKEY = 0x0312
_MOD_NOREPEAT = 0x4000
_MODIFIERS = {"Ctrl": 2, "Alt": 1, "Shift": 4}


def _bind(dll, name, result, *arguments):
    function = getattr(dll, name)
    function.restype = result
    function.argtypes = arguments
    return function


class _Windows:
    def __init__(self):
        user = ctypes.WinDLL("user32", use_last_error=True)
        controls = ctypes.WinDLL("comctl32", use_last_error=True)
        dwm = ctypes.WinDLL("dwmapi")
        self._enum = _bind(user, "EnumWindows", wt.BOOL, _ENUM_PROC, wt.LPARAM)
        self._get_pid = _bind(user, "GetWindowThreadProcessId", wt.DWORD, wt.HWND, ctypes.POINTER(wt.DWORD))
        self.ancestor = _bind(user, "GetAncestor", wt.HWND, wt.HWND, wt.UINT)
        self.set_property = _bind(user, "SetPropW", wt.BOOL, wt.HWND, wt.LPCWSTR, wt.HANDLE)
        self.get_property = _bind(user, "GetPropW", wt.HANDLE, wt.HWND, wt.LPCWSTR)
        self.remove_property = _bind(user, "RemovePropW", wt.HANDLE, wt.HWND, wt.LPCWSTR)
        self.allow_foreground = _bind(user, "AllowSetForegroundWindow", wt.BOOL, wt.DWORD)
        self.visible = _bind(user, "IsWindowVisible", wt.BOOL, wt.HWND)
        self.enabled = _bind(user, "IsWindowEnabled", wt.BOOL, wt.HWND)
        self._is_window = _bind(user, "IsWindow", wt.BOOL, wt.HWND)
        self.minimized = _bind(user, "IsIconic", wt.BOOL, wt.HWND)
        self._style = _bind(user, "GetWindowLongW", wt.LONG, wt.HWND, ctypes.c_int)
        self._show = _bind(user, "ShowWindowAsync", wt.BOOL, wt.HWND, ctypes.c_int)
        self.foreground = _bind(user, "SetForegroundWindow", wt.BOOL, wt.HWND)
        self._border = _bind(dwm, "DwmSetWindowAttribute", ctypes.c_long,
                             wt.HWND, wt.DWORD, ctypes.c_void_p, wt.DWORD)
        self._register = _bind(user, "RegisterHotKey", wt.BOOL, wt.HWND, ctypes.c_int, wt.UINT, wt.UINT)
        self._unregister = _bind(user, "UnregisterHotKey", wt.BOOL, wt.HWND, ctypes.c_int)
        self._attach = _bind(controls, "SetWindowSubclass", wt.BOOL, wt.HWND, _SUBCLASS_PROC, ctypes.c_size_t, ctypes.c_size_t)
        self._detach = _bind(controls, "RemoveWindowSubclass", wt.BOOL, wt.HWND, _SUBCLASS_PROC, ctypes.c_size_t)
        self._default = _bind(controls, "DefSubclassProc", ctypes.c_ssize_t, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)

    def windows(self):
        handles = []

        @_ENUM_PROC
        def collect(hwnd, _):
            handles.append(hwnd)
            return True

        if not self._enum(collect, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        return handles

    def pid(self, hwnd):
        owner = wt.DWORD()
        self._get_pid(hwnd, ctypes.byref(owner))
        return owner.value

    def tool_window(self, hwnd):
        return bool(self._style(hwnd, -20) & 0x80)  # GWL_EXSTYLE, WS_EX_TOOLWINDOW

    def restore(self, hwnd):
        self._show(hwnd, 9)  # SW_RESTORE

    def border(self, hwnd, color):
        # DWMWA_BORDER_COLOR (Windows 11); unsupported DWM versions return a failing HRESULT.
        value = wt.DWORD(color)
        return self._border(hwnd, 34, ctypes.byref(value), ctypes.sizeof(value)) >= 0

    def attach(self, hwnd, handle_hotkey):
        @_SUBCLASS_PROC
        def procedure(window, message, wparam, lparam, subclass_id, data):
            if message == _WM_HOTKEY and handle_hotkey(wparam):
                return 0
            return self._default(window, message, wparam, lparam)

        if not self._attach(hwnd, procedure, id(procedure), 0):
            raise OSError("Could not connect global shortcuts to the window.")
        return procedure  # must remain alive while Windows holds the callback

    def detach(self, hwnd, procedure):
        if not self._detach(hwnd, procedure, id(procedure)) and self._is_window(hwnd):
            raise OSError("Could not disconnect global shortcuts from this window.")

    def register(self, hwnd, token, modifiers, key):
        if not self._register(hwnd, token, modifiers, key):
            raise ctypes.WinError(ctypes.get_last_error())

    def unregister(self, hwnd, token):
        if not self._unregister(hwnd, token):
            raise ctypes.WinError(ctypes.get_last_error())


@cache
def _native():
    return _Windows()


def _manager_property(base):
    # Use the same case and path equivalence as the per-base application mutex.
    path = os.path.normcase(os.path.abspath(base))
    return "MultiClaude.Manager." + hashlib.sha256(os.fsencode(path)).hexdigest()


def mark_manager_window(root, base):
    """Mark the manager's top-level HWND for this portable base until Tk destroys it."""
    win = _native()
    key = _manager_property(base)
    hwnd = win.ancestor(root.winfo_id(), 2)  # GA_ROOT selects Tk's outer HWND, which EnumWindows sees.
    pid = os.getpid()
    if not hwnd or win.pid(hwnd) != pid or not win.set_property(hwnd, key, pid):
        return False

    def cleanup(event):
        if event.widget is root:
            win.remove_property(hwnd, key)

    try:
        root.bind("<Destroy>", cleanup, add="+")
    except tk.TclError:
        win.remove_property(hwnd, key)
        return False
    return True


def allow_manager_foreground(base):
    """Pass a shortcut sender's foreground permission to one matching manager, if Windows allows it."""
    win = _native()
    key = _manager_property(base)
    match = None
    try:
        for hwnd in win.windows():
            pid = win.get_property(hwnd, key)
            if not pid or pid >= 0xFFFFFFFF or win.pid(hwnd) != pid:
                continue
            if match is not None:
                return False
            match = hwnd, pid
        if match is None:
            return False
        hwnd, pid = match
        return bool(win.get_property(hwnd, key) == pid and win.pid(hwnd) == pid and win.allow_foreground(pid))
    except OSError:
        return False


def _profile_windows(win, pids):
    if pids:
        for hwnd in win.windows():
            if win.pid(hwnd) in pids and win.visible(hwnd) and not win.tool_window(hwnd):
                yield hwnd


def focus_profile_window(pids):
    """Restore and foreground an existing top-level window, respecting Windows focus restrictions."""
    win = _native()
    owners = frozenset(pid for pid in pids if pid > 0)
    result = FocusResult.MISSING
    for hwnd in _profile_windows(win, owners):
        if not win.enabled(hwnd):
            if win.pid(hwnd) in owners:
                result = FocusResult.BLOCKED
            continue
        if win.pid(hwnd) not in owners:
            continue
        if win.minimized(hwnd):
            win.restore(hwnd)
        return FocusResult.FOCUSED if win.foreground(hwnd) else FocusResult.BLOCKED
    return result


def set_profile_border(pids, color):
    """Set a #RRGGBB border (None restores the system color). Return the number accepted by DWM."""
    if color is None:
        value = 0xFFFFFFFF  # DWMWA_COLOR_DEFAULT
    else:
        if not isinstance(color, str) or not re.fullmatch(r"#[0-9a-fA-F]{6}", color):
            raise ValueError("Expected #RRGGBB or None.")
        value = int.from_bytes(bytes.fromhex(color[1:]), "little")  # Windows COLORREF is 0x00BBGGRR
    win = _native()
    owners = frozenset(pid for pid in pids if pid > 0)
    changed = 0
    for hwnd in _profile_windows(win, owners):
        if win.pid(hwnd) in owners and win.border(hwnd, value):
            changed += 1
    return changed


def normalize_shortcut(text):
    """Canonicalize a Ctrl/Alt shortcut with a letter, digit or F1–F24 (except reserved F12)."""
    if text is None:
        return ""
    if not isinstance(text, str):
        raise ValueError("Expected shortcut text.")
    if not text.strip():
        return ""
    parts = [part.strip().title() for part in text.split("+")]
    aliases = {"Control": "Ctrl", "Strg": "Ctrl", "Umschalt": "Shift"}
    modifiers = [aliases.get(part, part) for part in parts[:-1]]
    key = parts[-1].upper()
    if (len(modifiers) != len(set(modifiers)) or set(modifiers) - _MODIFIERS.keys()
            or not {"Ctrl", "Alt"}.intersection(modifiers)
            or not re.fullmatch(r"[A-Z0-9]|F(?:[1-9]|1[0-9]|2[0-4])", key) or key == "F12"):
        raise ValueError("Use Ctrl or Alt, optionally Shift, with a letter, digit or F1–F24 (except F12).")
    return "+".join([name for name in _MODIFIERS if name in modifiers] + [key])


class ProfileHotkeys:
    """Own one registration per profile on a Tk root. Call all methods from its UI thread."""
    def __init__(self, root, on_activate):
        self.root = root
        self._activate = on_activate
        self._hwnd = root.winfo_id()
        self._win = _native()
        self._by_profile = {}
        self._by_token = {}
        self._next = 1
        self._procedure = self._win.attach(self._hwnd, self._on_message)
        self._destroy_binding = root.bind("<Destroy>", self._on_destroy, add="+")

    def _on_destroy(self, event):
        if event.widget is self.root:
            self.close()

    def _on_message(self, token):
        if token not in self._by_token:
            return False
        try:
            self.root.after_idle(self._deliver, token)
        except tk.TclError:
            pass  # shutdown may already have destroyed the Tk interpreter
        return True

    def _deliver(self, token):
        if token in self._by_token:
            self._activate(self._by_token[token])

    def set(self, profile_id, shortcut):
        """Assign, change or clear a shortcut. A rejected replacement preserves the old registration."""
        if self._procedure is None:
            raise RuntimeError("Global shortcut manager is closed.")
        shortcut = normalize_shortcut(shortcut)
        old = self._by_profile.get(profile_id)
        if old and old[0] == shortcut:
            return shortcut
        if shortcut:
            if any(binding[0] == shortcut for binding in self._by_profile.values()):
                raise ValueError("This shortcut already belongs to another profile.")
            if self._next > 0xBFFF:  # Win32's application-owned ID range
                raise OSError("Restart the application to assign more shortcuts.")
            token = self._next
            *mods, key = shortcut.split("+")
            vk = ord(key) if len(key) == 1 else 0x70 + int(key[1:]) - 1
            self._win.register(self._hwnd, token, _MOD_NOREPEAT | sum(_MODIFIERS[m] for m in mods), vk)
            self._next += 1
        if old:
            try:
                self._win.unregister(self._hwnd, old[1])
            except OSError:
                if shortcut:
                    self._win.unregister(self._hwnd, token)
                raise
            del self._by_profile[profile_id]
            del self._by_token[old[1]]
        if shortcut:
            self._by_profile[profile_id] = shortcut, token
            self._by_token[token] = profile_id
        return shortcut

    def close(self):
        if self._procedure is None:
            return
        for token in self._by_token:
            try:
                self._win.unregister(self._hwnd, token)
            except OSError:
                pass  # Windows releases window-owned bindings on destruction, too
        self._by_profile.clear()
        self._by_token.clear()
        self._win.detach(self._hwnd, self._procedure)
        self._procedure = None
        self.root.unbind("<Destroy>", self._destroy_binding)
