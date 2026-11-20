"""Test the Windows notification icon lifecycle without ever adding an icon to Explorer."""
import ctypes
from ctypes import wintypes as wt
import tkinter as tk
import unittest
from unittest.mock import Mock, patch

import profile_tray


class TrayTests(unittest.TestCase):
    def setUp(self):
        self.backend = self.enterContext(patch.object(profile_tray, "_native"))
        self.win = self.backend.return_value
        self.win.icon.side_effect = [100, 101, 102]
        self.win.add.return_value = True
        self.root = Mock()
        self.root.winfo_id.return_value = 47
        self.callback = Mock()
        self.manager = profile_tray.ProfileTray(self.root, self.callback)
        self.addCleanup(self.manager.close)

    def test_disabled_profile_never_touches_shell(self):
        self.manager.set(7, "Privat", "#223344", False)
        self.backend.assert_not_called()

    def test_adding_updating_and_removing_an_icon_releases_each_handle_once(self):
        self.manager.set(7, "Privat", "#223344", True)
        self.manager.set(7, "Privat", "#223344", True)
        self.win.add.assert_called_once()
        self.manager.set(7, "Studio", "#ffbb22", True)
        self.win.modify.assert_called_once()
        self.win.destroy_icon.assert_called_once_with(100)
        self.manager.set(7, "Studio", "#ffbb22", False)
        self.win.remove.assert_called_once()
        self.win.destroy_icon.assert_called_with(101)
        self.manager.close()
        self.assertEqual(self.win.destroy_icon.call_count, 2)

    def test_restarts_restore_only_enabled_icons_and_activation_is_profile_specific(self):
        self.manager.set(7, "Privat", "#223344", True)
        self.manager.set(8, "Arbeit", "#335588", True)
        first_token, second_token = [call.args[1] for call in self.win.add.call_args_list]
        _, activate, restart = self.win.attach.call_args.args
        activate(first_token)
        pending, *args = self.root.after_idle.call_args.args
        self.manager.set(7, "Privat", "#223344", False)
        pending(*args)
        self.callback.assert_not_called()
        activate(second_token)
        pending, *args = self.root.after_idle.call_args.args
        pending(*args)
        self.callback.assert_called_once_with(8)
        self.win.add.reset_mock()
        restart()
        self.win.add.assert_called_once()
        self.assertEqual(self.win.add.call_args.args[1], second_token)

    def test_explorer_unavailable_at_start_is_retried_and_disabling_cancels_recovery(self):
        self.win.add.side_effect = [OSError("Explorer unavailable"), None, OSError("busy")]
        with self.assertRaises(OSError):
            self.manager.set(4, "Studio", "#111133", True)
        _, retry = self.root.after.call_args.args
        retry()
        self.assertEqual(self.win.add.call_count, 2)
        self.win.destroy_icon.assert_not_called()
        with self.assertRaises(OSError):
            self.manager.set(5, "Work", "#888833", True)
        _, retry = self.root.after.call_args.args
        self.manager.set(5, "Work", "#888833", False)
        retry()
        self.assertEqual(self.win.add.call_count, 3)
        self.manager.close()
        self.assertCountEqual([call.args[0] for call in self.win.destroy_icon.call_args_list], [100, 101])

    def test_failed_update_retries_changed_color_without_retaining_old_handles(self):
        self.manager.set(4, "Studio", "#111133", True)
        self.win.modify.side_effect = [OSError("temporarily busy"), None]
        with self.assertRaises(OSError):
            self.manager.set(4, "Redaktion", "#882299", True)
        self.win.destroy_icon.assert_called_once_with(100)
        self.win.add.side_effect = OSError("already in shell")
        _, retry = self.root.after.call_args.args
        retry()
        self.assertEqual(self.win.modify.call_args.args[-2:], ("Redaktion", 101))
        self.manager.close()
        self.win.destroy_icon.assert_called_with(101)

    def test_real_gdi_letter_icon_can_be_created_and_destroyed_without_publishing(self):
        win = self.backend()
        # This separate native backend never calls Shell_NotifyIcon.
        real = profile_tray._WindowsTray()
        for name, color in (("私", "#eeeebb"), ("Studio", "#235674")):
            handle = real.icon(name, color)
            self.assertTrue(handle)
            self.assertTrue(real.destroy_icon(handle))
        win.add.assert_not_called()

    def test_hidden_hwnd_receives_native_mouse_keyboard_and_explorer_restart_messages(self):
        root = tk.Tk()
        root.withdraw()
        self.addCleanup(root.destroy)
        real = profile_tray._WindowsTray()
        hwnd = real.window(root.winfo_id())
        mouse_or_key, restart = Mock(), Mock()
        proc = real.attach(hwnd, mouse_or_key, restart)
        self.addCleanup(real.detach, hwnd, proc)
        real._modern.add((hwnd, 23))
        send = profile_tray._bind(ctypes.windll.user32, "SendMessageW", ctypes.c_ssize_t,
                                 wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
        for event in (0x0400, 0x0401, 0x0202, 0x007B):
            send(hwnd, real.message, 0, (23 << 16) | event)
        self.assertEqual([call.args[0] for call in mouse_or_key.call_args_list], [23, 23])
        send(hwnd, real.restart_message, 0, 0)
        restart.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
