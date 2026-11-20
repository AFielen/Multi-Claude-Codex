"""Opt-in Windows notification-area icons for individual Multi Claude profiles."""
import ctypes
from ctypes import wintypes as wt
from dataclasses import dataclass
from functools import cache
import math
import re
import tkinter as tk

from windows_profiles import _bind, _SUBCLASS_PROC


class _NotifyData(ctypes.Structure):
    _fields_ = [
        ("size", wt.DWORD), ("window", wt.HWND), ("identifier", wt.UINT), ("flags", wt.UINT),
        ("message", wt.UINT), ("icon", wt.HICON), ("tip", wt.WCHAR * 128), ("state", wt.DWORD),
        ("state_mask", wt.DWORD), ("info", wt.WCHAR * 256), ("version", wt.UINT),
        ("title", wt.WCHAR * 64), ("info_flags", wt.DWORD), ("guid", ctypes.c_byte * 16),
        ("balloon_icon", wt.HICON),
    ]


class _BitmapHeader(ctypes.Structure):
    _fields_ = [
        ("size", wt.DWORD), ("width", wt.LONG), ("height", wt.LONG), ("planes", wt.WORD),
        ("bits", wt.WORD), ("compression", wt.DWORD), ("image_size", wt.DWORD),
        ("x_meter", wt.LONG), ("y_meter", wt.LONG), ("used", wt.DWORD), ("important", wt.DWORD),
    ]


class _IconInfo(ctypes.Structure):
    _fields_ = [("is_icon", wt.BOOL), ("x", wt.DWORD), ("y", wt.DWORD),
                ("mask", wt.HBITMAP), ("bitmap", wt.HBITMAP)]


class _WindowsTray:
    def __init__(self):
        user = ctypes.WinDLL("user32", use_last_error=True)
        gdi = ctypes.WinDLL("gdi32", use_last_error=True)
        controls = ctypes.WinDLL("comctl32", use_last_error=True)
        shell = ctypes.WinDLL("shell32", use_last_error=True)
        register = _bind(user, "RegisterWindowMessageW", wt.UINT, wt.LPCWSTR)
        self.message = register("MultiClaude.ProfileTray.Activation")
        self.restart_message = register("TaskbarCreated")
        if not self.message or not self.restart_message:
            raise ctypes.WinError(ctypes.get_last_error())
        self._modern = set()
        self._notify = _bind(shell, "Shell_NotifyIconW", wt.BOOL, wt.DWORD, ctypes.POINTER(_NotifyData))
        self._ancestor = _bind(user, "GetAncestor", wt.HWND, wt.HWND, wt.UINT)
        self._attach = _bind(controls, "SetWindowSubclass", wt.BOOL, wt.HWND, _SUBCLASS_PROC, ctypes.c_size_t, ctypes.c_size_t)
        self._detach = _bind(controls, "RemoveWindowSubclass", wt.BOOL, wt.HWND, _SUBCLASS_PROC, ctypes.c_size_t)
        self._is_window = _bind(user, "IsWindow", wt.BOOL, wt.HWND)
        self._default = _bind(controls, "DefSubclassProc", ctypes.c_ssize_t, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
        self._metric = _bind(user, "GetSystemMetrics", ctypes.c_int, ctypes.c_int)
        self._create_dc = _bind(gdi, "CreateCompatibleDC", wt.HDC, wt.HDC)
        self._delete_dc = _bind(gdi, "DeleteDC", wt.BOOL, wt.HDC)
        self._dib = _bind(gdi, "CreateDIBSection", wt.HBITMAP, wt.HDC, ctypes.POINTER(_BitmapHeader),
                          wt.UINT, ctypes.POINTER(ctypes.c_void_p), wt.HANDLE, wt.DWORD)
        self._mask = _bind(gdi, "CreateBitmap", wt.HBITMAP, ctypes.c_int, ctypes.c_int,
                           wt.UINT, wt.UINT, ctypes.c_void_p)
        self._select = _bind(gdi, "SelectObject", wt.HANDLE, wt.HDC, wt.HANDLE)
        self._delete = _bind(gdi, "DeleteObject", wt.BOOL, wt.HANDLE)
        self._font = _bind(gdi, "CreateFontW", wt.HFONT, *([ctypes.c_int] * 5), *([wt.DWORD] * 8), wt.LPCWSTR)
        self._bk_mode = _bind(gdi, "SetBkMode", ctypes.c_int, wt.HDC, ctypes.c_int)
        self._text_color = _bind(gdi, "SetTextColor", wt.DWORD, wt.HDC, wt.DWORD)
        self._draw = _bind(user, "DrawTextW", ctypes.c_int, wt.HDC, wt.LPCWSTR, ctypes.c_int, ctypes.POINTER(wt.RECT), wt.UINT)
        self._flush = _bind(gdi, "GdiFlush", wt.BOOL)
        self._create_icon = _bind(user, "CreateIconIndirect", wt.HICON, ctypes.POINTER(_IconInfo))
        self.destroy_icon = _bind(user, "DestroyIcon", wt.BOOL, wt.HICON)

    def window(self, tk_window):
        # Explorer broadcasts TaskbarCreated only to top-level windows, not Tk's inner HWND.
        return self._ancestor(tk_window, 2) or tk_window  # GA_ROOT

    def attach(self, hwnd, activate, restart):
        @_SUBCLASS_PROC
        def procedure(window, message, wparam, lparam, subclass_id, data):
            if message == self.restart_message:
                restart()
            elif message == self.message:
                token, event = (lparam >> 16) & 0xFFFF, lparam & 0xFFFF
                if (hwnd, token) in self._modern:
                    if event in (0x0400, 0x0401):  # NIN_SELECT, NIN_KEYSELECT
                        activate(token)
                elif event in (0x0202, 0x0401):  # legacy WM_LBUTTONUP or keyboard selection
                    activate(wparam)
                return 0
            return self._default(window, message, wparam, lparam)

        if not self._attach(hwnd, procedure, id(procedure), 0):
            raise OSError("Could not connect profile icons to the taskbar.")
        return procedure

    def detach(self, hwnd, procedure):
        if not self._detach(hwnd, procedure, id(procedure)) and self._is_window(hwnd):
            raise OSError("Could not disconnect profile icons from the taskbar.")

    def _data(self, hwnd, token, name, icon):
        nid = _NotifyData()
        nid.size, nid.window, nid.identifier = ctypes.sizeof(nid), hwnd, token
        nid.flags, nid.message, nid.icon = 1 | 2 | 4 | 0x80, self.message, icon  # MESSAGE, ICON, TIP, SHOWTIP
        nid.tip = name.encode("utf-16-le", "replace")[:254].decode("utf-16-le", "ignore")
        return nid

    def add(self, hwnd, token, name, icon):
        nid = self._data(hwnd, token, name, icon)
        if not self._notify(0, ctypes.byref(nid)):  # NIM_ADD
            raise OSError("The Windows notification area is unavailable.")
        nid.version = 4
        self._modern.discard((hwnd, token))
        if self._notify(4, ctypes.byref(nid)):  # NIM_SETVERSION
            self._modern.add((hwnd, token))

    def modify(self, hwnd, token, name, icon):
        nid = self._data(hwnd, token, name, icon)
        if not self._notify(1, ctypes.byref(nid)):  # NIM_MODIFY
            raise OSError("The profile icon could not be updated.")

    def remove(self, hwnd, token):
        nid = self._data(hwnd, token, "", None)
        self._notify(2, ctypes.byref(nid))  # NIM_DELETE (already gone if Explorer restarted)
        self._modern.discard((hwnd, token))

    def icon(self, name, color):
        """Draw an antialiased colored badge and one Unicode initial, with a native Windows font."""
        size = max(16, self._metric(49))  # SM_CXSMICON
        r, g, b = bytes.fromhex(color[1:])
        bg = (r << 16) | (g << 8) | b
        initial = next((c for c in name if c.isalnum()), "?").upper()[:1]
        dc = self._create_dc(None)
        if not dc:
            raise ctypes.WinError(ctypes.get_last_error())
        bitmap = mask = font = old_bitmap = old_font = None
        try:
            bits = ctypes.c_void_p()
            header = _BitmapHeader(ctypes.sizeof(_BitmapHeader), size, -size, 1, 32)
            bitmap = self._dib(dc, ctypes.byref(header), 0, ctypes.byref(bits), None, 0)
            mask_bits = bytes(((size + 15) // 16) * 2 * size)
            mask = self._mask(size, size, 1, 1, mask_bits)
            font = self._font(-round(size * .75), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI")
            if not (bitmap and mask and font and bits.value):
                raise ctypes.WinError(ctypes.get_last_error())
            old_bitmap = self._select(dc, bitmap)
            old_font = self._select(dc, font)
            pixels = (wt.DWORD * (size * size)).from_address(bits.value)
            pixels[:] = [bg] * len(pixels)
            self._bk_mode(dc, 1)  # TRANSPARENT
            ink = 0 if .299 * r + .587 * g + .114 * b >= 145 else 0xFFFFFF
            self._text_color(dc, ink)
            rect = wt.RECT(0, 0, size, size)
            self._draw(dc, initial, -1, ctypes.byref(rect), 0x01 | 0x04 | 0x20 | 0x800)  # centered, single line, no prefix
            self._flush()
            # A 32-bit icon uses premultiplied BGRA. GDI's text renderer leaves the alpha byte unset.
            middle = (size - 1) / 2
            for y in range(size):
                for x in range(size):
                    i = y * size + x
                    a = min(255, max(0, round(255 * (size / 2 + .25 - math.hypot(x - middle, y - middle)))))
                    rgb = pixels[i]
                    red, green, blue = (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255
                    pixels[i] = (a << 24) | ((red * a // 255) << 16) | ((green * a // 255) << 8) | (blue * a // 255)
            self._select(dc, old_bitmap)
            old_bitmap = None
            info = _IconInfo(True, 0, 0, mask, bitmap)
            handle = self._create_icon(ctypes.byref(info))
            if not handle:
                raise ctypes.WinError(ctypes.get_last_error())
            return handle
        finally:
            if old_bitmap:
                self._select(dc, old_bitmap)
            if old_font:
                self._select(dc, old_font)
            for obj in (font, mask, bitmap):
                if obj:
                    self._delete(obj)
            self._delete_dc(dc)


@cache
def _native():
    return _WindowsTray()


@dataclass
class _Entry:
    token: int
    name: str
    color: str
    icon: int


class ProfileTray:
    def __init__(self, root, on_activate):
        self.root = root
        self._activate = on_activate
        self._entries = {}
        self._by_token = {}
        self._pending = set()
        self._next = 1
        self._win = self._hwnd = self._procedure = self._retry = None
        self._closed = False
        self._binding = root.bind("<Destroy>", self._on_destroy, add="+")

    def _on_destroy(self, event):
        if event.widget is self.root:
            self.close()

    def _schedule(self, token):
        if token in self._by_token:
            try:
                self.root.after_idle(self._deliver, token)
            except tk.TclError:
                pass

    def _deliver(self, token):
        if token in self._by_token:
            self._activate(self._by_token[token])

    def _restore(self):
        if self._retry is not None:
            self.root.after_cancel(self._retry)
        self._pending.update(self._entries)
        self._retry_pending()

    def _arm_retry(self):
        if self._pending and self._retry is None:
            self._retry = self.root.after(3000, self._retry_pending)

    def _retry_pending(self):
        self._retry = None
        for profile_id in tuple(self._pending):
            try:
                self._recover(profile_id)
            except OSError:
                pass  # Explorer can still be initializing after its TaskbarCreated broadcast.
        self._arm_retry()

    def _recover(self, profile_id):
        entry = self._entries[profile_id]
        args = (self._hwnd, entry.token, entry.name, entry.icon)
        try:
            self._win.add(*args)
        except OSError:
            # An interrupted modify leaves an existing shell entry; Explorer rejects a duplicate add.
            self._win.modify(*args)
        self._pending.discard(profile_id)

    def set(self, profile_id, name, color, enabled):
        if self._closed:
            raise RuntimeError("Profile tray is closed.")
        old = self._entries.get(profile_id)
        if not enabled:
            if old:
                self._pending.discard(profile_id)
                self._win.remove(self._hwnd, old.token)
                self._win.destroy_icon(old.icon)
                del self._entries[profile_id], self._by_token[old.token]
            return
        if not isinstance(color, str) or not re.fullmatch(r"#[0-9A-Fa-f]{6}", color):
            raise ValueError("Expected a #RRGGBB profile color.")
        if not isinstance(name, str) or not name.strip() or "\0" in name:
            raise ValueError("Expected a profile name.")
        color = color.lower()
        if old and (old.name, old.color) == (name, color):
            if profile_id in self._pending:
                self._recover(profile_id)
            return
        if self._procedure is None:
            self._win = _native()
            self._hwnd = self._win.window(self.root.winfo_id())
            self._procedure = self._win.attach(self._hwnd, self._schedule, self._restore)
        token = old.token if old else self._next
        if token > 0xFFFF:
            raise OSError("Restart the application to add more profile icons.")
        icon = self._win.icon(name, color)
        self._entries[profile_id] = _Entry(token, name, color, icon)
        self._by_token[token] = profile_id
        if not old:
            self._next += 1
        try:
            if old:
                self._win.modify(self._hwnd, token, name, icon)
            else:
                self._win.add(self._hwnd, token, name, icon)
            self._pending.discard(profile_id)
        except OSError:
            self._pending.add(profile_id)
            self._arm_retry()
            raise
        finally:
            if old:
                self._win.destroy_icon(old.icon)

    def close(self):
        if self._closed:
            return
        if self._retry is not None:
            self.root.after_cancel(self._retry)
            self._retry = None
        for profile_id in list(self._entries):
            self.set(profile_id, "", None, False)
        if self._procedure is not None:
            self._win.detach(self._hwnd, self._procedure)
            self._procedure = None
        self.root.unbind("<Destroy>", self._binding)
        self._closed = True
