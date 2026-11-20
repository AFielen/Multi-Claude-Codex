"""Window and shortcut dispatch tests using mocks or a private test desktop."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import ctypes
from ctypes import wintypes as wt
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import tkinter as tk
import unittest
from unittest.mock import Mock, patch
import uuid

import windows_profiles as profiles


class WindowTests(unittest.TestCase):
    def setUp(self):
        self.win = self.enterContext(patch.object(profiles, "_native")).return_value
        self.win.windows.return_value = [10, 20, 30, 40]
        self.win.pid.side_effect = {10: 1, 20: 2, 30: 2, 40: 2}.__getitem__
        self.win.visible.side_effect = lambda hwnd: hwnd != 20
        self.win.tool_window.side_effect = lambda hwnd: hwnd == 30
        self.win.enabled.return_value = True

    def test_restores_and_focuses_only_the_requested_profile(self):
        self.win.minimized.return_value = True
        self.win.foreground.return_value = True
        self.assertEqual(profiles.focus_profile_window([2]), profiles.FocusResult.FOCUSED)
        self.win.restore.assert_called_once_with(40)
        self.win.foreground.assert_called_once_with(40)

    def test_focus_refusal_and_absent_window_are_distinct(self):
        self.win.foreground.return_value = False
        self.assertEqual(profiles.focus_profile_window([2]), profiles.FocusResult.BLOCKED)
        self.assertEqual(profiles.focus_profile_window([99]), profiles.FocusResult.MISSING)
        self.win.foreground.assert_called_once_with(40)

    def test_disabled_window_is_present_but_not_focusable(self):
        self.win.enabled.return_value = False
        self.assertEqual(profiles.focus_profile_window([2]), profiles.FocusResult.BLOCKED)
        self.win.foreground.assert_not_called()

    def test_rechecks_window_ownership_before_acting(self):
        self.win.windows.return_value = [40]
        self.win.pid.side_effect = [2, 99]
        self.assertEqual(profiles.focus_profile_window([2]), profiles.FocusResult.MISSING)
        self.win.foreground.assert_not_called()
        self.win.restore.assert_not_called()

    def test_native_border_converts_color_resets_and_accepts_unsupported_windows(self):
        self.win.border.return_value = True
        self.assertEqual(profiles.set_profile_border([2], "#123456"), 1)
        self.win.border.assert_called_once_with(40, 0x563412)
        self.win.border.return_value = False
        self.assertEqual(profiles.set_profile_border([2], None), 0)
        self.win.border.assert_called_with(40, 0xFFFFFFFF)


class HotkeyTests(unittest.TestCase):
    def setUp(self):
        self.win = self.enterContext(patch.object(profiles, "_native")).return_value
        self.root = Mock()
        self.root.winfo_id.return_value = 55
        self.activate = Mock()
        self.keys = profiles.ProfileHotkeys(self.root, self.activate)
        self.addCleanup(self.keys.close)

    def test_rejects_typing_keys_without_modifiers_and_reserved_shortcuts(self):
        for text in ("X", "Shift+X", "Ctrl+Alt+F12", "Alt+Tab", "Ctrl+A+B", "Ctrl+Ctrl+A"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                self.keys.set(7, text)
        self.win.register.assert_not_called()

    def test_conflict_keeps_previous_binding_and_dispatches_via_tk(self):
        self.assertEqual(self.keys.set(7, "alt + ctrl + 1"), "Ctrl+Alt+1")
        _, token, modifiers, key = self.win.register.call_args.args
        self.assertEqual((modifiers, key), (0x4003, ord("1")))
        self.win.register.side_effect = OSError("already registered")
        with self.assertRaises(OSError):
            self.keys.set(7, "Ctrl+Alt+K")
        self.win.unregister.assert_not_called()
        handler = self.win.attach.call_args.args[1]
        self.assertTrue(handler(token))
        callback, *args = self.root.after_idle.call_args.args
        callback(*args)
        self.activate.assert_called_once_with(7)

    def test_duplicate_is_rejected_and_queued_shortcut_cannot_activate_deleted_profile(self):
        self.keys.set(3, "Ctrl+Alt+A")
        self.keys.set(3, "Ctrl+Alt+a")
        self.win.register.assert_called_once()
        token = self.win.register.call_args.args[1]
        with self.assertRaises(ValueError):
            self.keys.set(4, "Ctrl+Alt+A")
        handler = self.win.attach.call_args.args[1]
        handler(token)
        self.keys.set(3, "")
        callback, *args = self.root.after_idle.call_args.args
        callback(*args)
        self.activate.assert_not_called()
        self.assertFalse(handler(token))

    def test_close_releases_all_keys_and_subclass_once(self):
        self.keys.set(1, "Ctrl+Alt+1")
        self.keys.set(2, "Alt+F8")
        self.keys.close()
        self.keys.close()
        self.assertEqual(self.win.unregister.call_count, 2)
        self.win.detach.assert_called_once()


class TkMessageTests(unittest.TestCase):
    def test_manager_marker_uses_native_window_and_portable_base_and_is_removed(self):
        root = tk.Tk()
        root.withdraw()
        root.update_idletasks()
        self.addCleanup(lambda: root.destroy() if root.tk.call("info", "commands", ".") else None)
        win = profiles._native()
        hwnd = win.ancestor(root.winfo_id(), 2)
        home = r"C:\Synthetic\ManagerOne"
        with patch.object(win, "remove_property", wraps=win.remove_property) as remove, \
                patch.object(win, "windows", return_value=[hwnd]), patch.object(win, "allow_foreground", return_value=True) as allow:
            self.assertTrue(profiles.mark_manager_window(root, home))
            self.assertFalse(profiles.allow_manager_foreground(r"C:\Synthetic\ManagerTwo"))
            self.assertTrue(profiles.allow_manager_foreground(r"c:\synthetic\sub\..\managerone"))
            allow.assert_called_once_with(os.getpid())
            root.destroy()
            remove.assert_called_once()
            self.assertFalse(profiles.allow_manager_foreground(home))

    def test_hidden_fixture_receives_native_message_and_unregisters_on_destroy(self):
        root = tk.Tk()
        root.withdraw()
        def cleanup():
            try:
                root.destroy()
            except tk.TclError:
                pass
        self.addCleanup(cleanup)
        activate = Mock()
        native = profiles._native()
        # Keep all real global key combinations untouched. Only this fixture's HWND receives a message.
        with patch.object(native, "register") as register, patch.object(native, "unregister") as unregister:
            manager = profiles.ProfileHotkeys(root, activate)
            manager.set(23, "Ctrl+Alt+G")
            hwnd, token, _, _ = register.call_args.args
            send = ctypes.windll.user32.SendMessageW
            send.argtypes = (wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
            send.restype = ctypes.c_ssize_t
            send(hwnd, 0x0312, token, 0)
            root.update_idletasks()
            activate.assert_called_once_with(23)
            root.destroy()
            unregister.assert_called_once_with(hwnd, token)
            manager.close()


class ManagerIdentityTests(unittest.TestCase):
    def test_ordinary_long_path_keeps_previous_mutex_and_window_property(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp).resolve() / "Normal portable home"
            base.mkdir()
            previous = os.path.normcase(os.path.normpath(str(base)))
            self.assertEqual(profiles._manager_property(base),
                             "MultiClaude.Manager." + hashlib.sha256(os.fsencode(previous)).hexdigest())
            self.assertEqual(profiles.manager_mutex_name(base),
                             "MultiClaude_" + hashlib.md5(previous.encode()).hexdigest()[:16])

    def test_short_and_long_names_select_same_window_mutex_and_update_guard(self):
        from recursos import prepare_portable_update as updater
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        short_name = profiles._bind(kernel, "GetShortPathNameW", wt.DWORD, wt.LPCWSTR, wt.LPWSTR, wt.DWORD)
        create = profiles._bind(kernel, "CreateMutexW", wt.HANDLE, ctypes.c_void_p, wt.BOOL, wt.LPCWSTR)
        close = profiles._bind(kernel, "CloseHandle", wt.BOOL, wt.HANDLE)
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp).resolve() / "Example portable manager directory"
            base.mkdir()
            size = short_name(str(base), None, 0)
            if not size:
                self.skipTest("8.3 names unavailable on the fixture volume")
            alias = ctypes.create_unicode_buffer(size)
            self.assertTrue(short_name(str(base), alias, size))
            if os.path.normcase(str(base)) == os.path.normcase(alias.value):
                self.skipTest("8.3 name generation disabled on the fixture volume")
            self.assertTrue(os.path.samefile(base, alias.value))
            self.assertEqual(profiles._manager_property(base), profiles._manager_property(alias.value))
            mutex = profiles.manager_mutex_name(base)
            self.assertEqual(mutex, profiles.manager_mutex_name(alias.value))
            handle = create(None, False, mutex)
            self.assertTrue(handle)
            try:
                with self.assertRaisesRegex(RuntimeError, "still running"):
                    updater._require_idle(Path(alias.value))
            finally:
                close(handle)


class ForegroundPermissionTests(unittest.TestCase):
    def setUp(self):
        self.win = self.enterContext(patch.object(profiles, "_native")).return_value
        self.base = r"C:\Synthetic\Portable"

    def test_ambiguous_markers_and_invalid_or_reassigned_owners_receive_no_permission(self):
        self.win.windows.return_value = [11, 12]
        for marks, pids in (([41, 42], [41, 42]), ([0xFFFFFFFF, None], []), ([None, 47], [48]),
                            ([41, None, None], [41]), ([41, None, 41], [41, 49])):
            with self.subTest(marks=marks, pids=pids):
                self.win.get_property.side_effect = marks
                self.win.pid.side_effect = pids
                self.assertFalse(profiles.allow_manager_foreground(self.base))
                self.win.allow_foreground.assert_not_called()

    def test_os_refusal_is_reported_without_forcing_foreground(self):
        self.win.windows.return_value = [11]
        self.win.get_property.return_value = self.win.pid.return_value = 41
        self.win.allow_foreground.return_value = False
        self.assertFalse(profiles.allow_manager_foreground(self.base))
        self.win.allow_foreground.assert_called_once_with(41)
        self.win.foreground.assert_not_called()


def _private_test_window(desktop_name, base):
    user = ctypes.WinDLL("user32", use_last_error=True)
    open_desktop = profiles._bind(user, "OpenDesktopW", wt.HANDLE, wt.LPCWSTR, wt.DWORD, wt.BOOL, wt.DWORD)
    set_desktop = profiles._bind(user, "SetThreadDesktop", wt.BOOL, wt.HANDLE)
    desktop = open_desktop(desktop_name, 0, False, 0x83)  # READOBJECTS | CREATEWINDOW | WRITEOBJECTS
    if not desktop or not set_desktop(desktop):
        raise ctypes.WinError(ctypes.get_last_error())
    root = tk.Tk()
    root.title("Synthetic profile focus fixture")
    root.update()
    if not profiles.mark_manager_window(root, base):
        raise RuntimeError("Could not mark synthetic manager window.")
    ancestor = profiles._bind(user, "GetAncestor", wt.HWND, wt.HWND, wt.UINT)
    print(ancestor(root.winfo_id(), 2), flush=True)
    root.after(15000, root.destroy)
    root.mainloop()


class PrivateDesktopTests(unittest.TestCase):
    def test_restores_only_window_owned_by_selected_process(self):
        # A thread that has already created a Tk window cannot change desktops.
        with ThreadPoolExecutor(max_workers=1) as worker:
            worker.submit(self.check_restore).result(timeout=20)

    def check_restore(self):
        user = ctypes.WinDLL("user32", use_last_error=True)
        create = profiles._bind(user, "CreateDesktopW", wt.HANDLE, wt.LPCWSTR, wt.LPCWSTR,
                                ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.c_void_p)
        attach = profiles._bind(user, "SetThreadDesktop", wt.BOOL, wt.HANDLE)
        close = profiles._bind(user, "CloseDesktop", wt.BOOL, wt.HANDLE)
        current = profiles._bind(user, "GetThreadDesktop", wt.HANDLE, wt.DWORD)
        original = current(ctypes.windll.kernel32.GetCurrentThreadId())
        name = "MultiClaudeFocusTest_" + uuid.uuid4().hex
        desktop = create(name, None, None, 0, 0x83, None)  # No DESKTOP_SWITCHDESKTOP access.
        if not desktop:
            raise ctypes.WinError(ctypes.get_last_error())
        with ExitStack() as cleanup:
            cleanup.callback(close, desktop)
            if not attach(desktop):
                raise ctypes.WinError(ctypes.get_last_error())
            cleanup.callback(attach, original)
            children, handles = [], []
            bases = [Path.cwd() / f"{name}-{index}" for index in range(2)]
            for base in bases:
                # The base executable owns the GUI PID directly, including when tests run inside a venv.
                cmd = [sys._base_executable, "-B", "-c", "import sys; from recursos.test_windows_profiles "
                       "import _private_test_window; _private_test_window(*sys.argv[1:])", name, str(base)]
                child = cleanup.enter_context(subprocess.Popen(cmd, stdout=subprocess.PIPE, text=True,
                                    cwd=Path(__file__).resolve().parents[1], creationflags=subprocess.CREATE_NO_WINDOW))
                cleanup.callback(child.terminate)
                children.append(child)
                handles.append(int(child.stdout.readline()))
            native = profiles._native()
            show = profiles._bind(user, "ShowWindowAsync", wt.BOOL, wt.HWND, ctypes.c_int)
            def wait_for(predicate):
                deadline = time.monotonic() + 2
                while not predicate() and time.monotonic() < deadline:
                    time.sleep(.01)
                self.assertTrue(predicate())
            for index, child in enumerate(children):
                with patch.object(native, "allow_foreground", return_value=True) as grant:
                    self.assertTrue(profiles.allow_manager_foreground(bases[index]))
                    grant.assert_called_once_with(child.pid)
                for hwnd in handles:
                    show(hwnd, 6)  # SW_MINIMIZE, both synthetic HWNDs only
                wait_for(lambda: all(native.minimized(h) for h in handles))
                result = profiles.focus_profile_window([child.pid])
                self.assertIn(result, (profiles.FocusResult.FOCUSED, profiles.FocusResult.BLOCKED))
                wait_for(lambda: not native.minimized(handles[index]))
                self.assertTrue(native.minimized(handles[1 - index]))


if __name__ == "__main__":
    unittest.main()
