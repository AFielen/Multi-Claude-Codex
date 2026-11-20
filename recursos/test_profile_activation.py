"""Profile activation across the application, SQLite and hotkey dispatcher using fictional processes."""
import importlib.machinery
import importlib.util
import os
from pathlib import Path
import tempfile
from types import MethodType, SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import profile_settings
import windows_profiles


class ProfileActivationTests(unittest.TestCase):
    def setUp(self):
        folder = self.enterContext(tempfile.TemporaryDirectory())
        self.enterContext(patch.dict(os.environ, {"MULTI_CLAUDE_HOME": folder}))
        source = Path(__file__).resolve().parents[1] / "Multi Claude.pyw"
        loader = importlib.machinery.SourceFileLoader("activation_fixture", str(source))
        self.app = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
        loader.exec_module(self.app)
        with self.app.db() as con:
            for name in ("Personal", "Work"):
                result = con.execute("INSERT INTO perfis(nome,pasta) VALUES (?,?)", (name, os.path.join("dados", name)))
                profile_settings.add(con, result.lastrowid)
        self.native = self.enterContext(patch.object(windows_profiles, "_native")).return_value
        self.focus = self.enterContext(patch.object(windows_profiles, "focus_profile_window"))
        self.processes = self.enterContext(patch.object(self.app, "_claudes_com_pasta", return_value=[]))
        self.ui = self.start_manager()
        self.personal, self.work = self.ui.perfis()

    def start_manager(self):
        ui = SimpleNamespace(
            shortcuts={}, shortcut_errors={}, pending_open={}, selected=None, login_perfil=None,
            precisa_claude=Mock(return_value=True), launch_desktop=Mock(), feedback=Mock(), after=Mock(),
            confirmar=Mock(return_value=True), select_profile=Mock(), t=lambda key, **values: key,
        )
        for name in ("perfis", "abrir", "sync_shortcuts", "activate_id", "excluir"):
            setattr(ui, name, MethodType(getattr(self.app.App, name), ui))
        ui.root = Mock()
        ui.root.winfo_id.return_value = 55
        ui.root.after_idle.side_effect = lambda callback, *args: callback(*args)
        ui.hotkeys = windows_profiles.ProfileHotkeys(ui.root, ui.activate_id)
        self.addCleanup(ui.hotkeys.close)

        def reload_preferences():
            with self.app.db() as con:
                ui.options = profile_settings.all_settings(con)
            ui.sync_shortcuts()

        ui.forcar_estado = reload_preferences
        reload_preferences()
        return ui

    def test_existing_profile_uses_all_its_main_pids_and_never_launches_another(self):
        self.processes.return_value = [(31, self.personal[2]), (40, self.work[2]), (32, self.personal[2])]
        # The last PID for Personal has no window; the first owns its visible main window.
        self.focus.side_effect = lambda pids: (
            windows_profiles.FocusResult.FOCUSED if 31 in pids else windows_profiles.FocusResult.MISSING
        )
        self.ui.abrir(self.personal)
        self.assertEqual(set(self.focus.call_args.args[0]), {31, 32})
        self.ui.launch_desktop.assert_not_called()

    def test_existing_window_can_be_focused_when_launcher_is_missing(self):
        self.ui.precisa_claude.return_value = False
        self.processes.return_value = [(31, self.personal[2])]
        self.focus.return_value = windows_profiles.FocusResult.FOCUSED
        self.ui.abrir(self.personal)
        self.focus.assert_called_once()
        self.ui.launch_desktop.assert_not_called()

    def test_foreground_denial_keeps_both_instances_untouched(self):
        self.processes.return_value = [(31, self.personal[2]), (40, self.work[2])]
        self.focus.return_value = windows_profiles.FocusResult.BLOCKED
        self.ui.activate_id(self.work[0])
        self.ui.select_profile.assert_called_once_with(self.work[0])
        self.assertEqual(list(self.focus.call_args.args[0]), [40])
        self.ui.launch_desktop.assert_not_called()
        self.ui.feedback.assert_called_once_with("focus_blocked")
        self.assertEqual(self.ui.pending_open, {})

    def test_repeat_open_is_coalesced_per_profile(self):
        self.focus.return_value = windows_profiles.FocusResult.MISSING
        self.ui.abrir(self.personal)
        self.ui.abrir(self.personal)
        self.ui.abrir(self.work)
        self.assertEqual([c.args[0] for c in self.ui.launch_desktop.call_args_list], [self.personal, self.work])

    def test_shortcuts_restore_from_disk_and_deletion_only_releases_target(self):
        first_bindings = [c.args[2:] for c in self.native.register.call_args_list]
        self.assertEqual(len(first_bindings), 2)
        self.ui.hotkeys.close()
        self.native.register.reset_mock()
        self.native.unregister.reset_mock()
        restarted = self.start_manager()
        records = [c.args for c in self.native.register.call_args_list]
        self.assertEqual([record[2:] for record in records], first_bindings)
        handler = self.native.attach.call_args.args[1]
        personal_key, work_key = (record[1] for record in records)
        self.focus.return_value = windows_profiles.FocusResult.MISSING
        restarted.excluir(self.personal)
        self.native.unregister.assert_called_once_with(55, personal_key)
        self.assertFalse(handler(personal_key))
        self.assertTrue(handler(work_key))
        restarted.launch_desktop.assert_called_once_with(self.work)
        self.assertEqual(restarted.perfis(), [self.work])


if __name__ == "__main__":
    unittest.main()
