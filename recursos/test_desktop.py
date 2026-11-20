"""Windows regressions using fictional profiles, no live Claude processes or registry changes."""
import importlib.machinery
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

loader = importlib.machinery.SourceFileLoader("desktop", str(Path(__file__).resolve().parents[1] / "Multi Claude.pyw"))
spec = importlib.util.spec_from_loader(loader.name, loader)
app = importlib.util.module_from_spec(spec)
loader.exec_module(app)


class ProfileStateTests(unittest.TestCase):
    def test_configuration_is_used_when_log_is_missing(self):
        with tempfile.TemporaryDirectory() as temp:
            Path(temp, "config.json").write_text('{"lastKnownAccountUuid":"fictional"}', encoding="utf-8")
            self.assertTrue(app.esta_logado(temp))

    def test_invalid_configuration_does_not_break_status_updates(self):
        with tempfile.TemporaryDirectory() as temp:
            (Path(temp) / "logs").mkdir()
            Path(temp, "logs", "main.log").touch()
            Path(temp, "config.json").write_text("[]", encoding="utf-8")
            self.assertFalse(app.esta_logado(temp))

    def test_sign_out_failure_preserves_status_and_configuration(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            (folder / "Network").mkdir()
            (folder / "Network" / "Cookies").mkdir()  # deterministic file removal failure
            (folder / "logs").mkdir()
            (folder / "logs" / "main.log").touch()
            cfg = folder / "config.json"
            cfg.write_text('{"lastKnownAccountUuid":"fictional"}', encoding="utf-8")
            with self.assertRaises(OSError):
                app.deslogar(temp)
            self.assertEqual((folder / "logs" / "main.log").read_bytes(), b"")
            self.assertTrue(app.esta_logado(temp))

    def test_successful_local_sign_out_keeps_unrelated_preferences(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            (folder / "Network").mkdir()
            (folder / "Network" / "Cookies").touch()
            cfg = folder / "config.json"
            cfg.write_text('{"lastKnownAccountUuid":"demo","theme":"dark"}', encoding="utf-8")
            app.deslogar(temp)
            self.assertFalse((folder / "Network" / "Cookies").exists())
            self.assertEqual(json.loads(cfg.read_text(encoding="utf-8")), {"theme": "dark"})


class DesktopWindowTests(unittest.TestCase):
    def test_manual_login_dialog_names_and_launches_only_the_selected_profile(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp) / "Portable Test"
            paths = {"BASE": str(folder), "DB": str(folder / "perfis.db"), "LOG": str(folder / "log.txt"),
                     "FOTOS": str(folder / "profile_pictures"), "DADOS": str(folder / "dados")}
            with patch.multiple(app, **paths), patch.object(app, "achar_claude", return_value="Claude.exe"), patch.object(
                app, "claudes_abertos", return_value={}
            ), patch.object(app.subprocess, "Popen") as launch:
                app.cfg_set("idioma", "en")
                app.cfg_set("tema", "old-unknown-theme")
                app.cfg_set("geometria", "old-unknown-geometry")
                ui = app.App()
                try:
                    ui.withdraw()
                    with app.db() as con:
                        ui._inserir(con, "First")
                        ui._inserir(con, "Second")
                    first, second = ui.perfis()
                    ui.login(first)
                    ui.login(second)
                    with patch.object(app, "dialogo", return_value="claude://login/fixture") as enter:
                        ui.colar_link()
                    self.assertIn("Second", enter.call_args.args[1])
                    self.assertTrue(enter.call_args.kwargs["ocultar"])
                    args = launch.call_args.args[0]
                    self.assertEqual(args, ["Claude.exe", "--user-data-dir=" + second[2], "claude://login/fixture"])
                    self.assertIsNone(ui.login_perfil)
                    self.assertIsNone(app.cfg_get("login_url"))
                    self.assertNotIn(b"claude://", Path(app.LOG).read_bytes() if Path(app.LOG).exists() else b"")
                finally:
                    for timer in ui.tk.splitlist(ui.tk.call("after", "info")):
                        ui.after_cancel(timer)
                    ui.destroy()


class LifecycleTests(unittest.TestCase):
    def test_empty_profile_list_does_not_inspect_running_processes(self):
        ui = Mock()
        ui.perfis.return_value = []
        with patch.object(app, "claudes_abertos") as inspect:
            self.assertEqual(app.App.calcular_estado(ui), {})
        inspect.assert_not_called()

    def test_expired_dialog_cannot_submit_to_another_profile(self):
        ui = Mock()
        ui.login_perfil = (1, "Demo", "profile-one", None)
        def expire(*args, **kwargs):
            ui.login_perfil = None
            return "claude://login/"
        with patch.object(app, "dialogo", side_effect=expire), patch.object(app, "abrir_claude") as launch:
            app.App.colar_link(ui)
        launch.assert_not_called()

    def test_manual_submission_requires_a_login_destination(self):
        ui = Mock()
        ui.login_perfil = (1, "Demo", "profile-one", None)
        for invalid in ("claude://settings", "claude://login/", "claude://claude.ai/login", "claude://claude.ai/login?"):
            with self.subTest(invalid=invalid), patch.object(app, "dialogo", side_effect=[invalid, None]), patch.object(
                app, "abrir_claude"
            ) as launch:
                app.App.colar_link(ui)
                launch.assert_not_called()

    def test_sign_in_never_starts_a_callback_watcher_or_reads_the_clipboard(self):
        ui = Mock()
        ui.login_inicio = None
        with patch.object(app, "VigiaProcessos", create=True) as watcher, patch.object(
            app, "abrir_claude"
        ), patch.object(app, "cfg_set"), patch.object(app, "log"):
            app.App.login(ui, (1, "Demo", "demo-profile", None))
        watcher.assert_not_called()
        ui.ler_clip.assert_not_called()
        ui.clipboard_get.assert_not_called()

    def test_missing_profile_path_cannot_launch_default_claude(self):
        with patch.object(app.subprocess, "Popen") as launch:
            with self.assertRaises((OSError, ValueError)):
                app.abrir_claude("Claude.exe", "")
        launch.assert_not_called()

    def test_process_matching_uses_exact_windows_profile_argument(self):
        directory = r"C:\Example Directory\Profiles\Profile One"
        normal = subprocess.list2cmdline(["Claude.exe", "--user-data-dir=" + directory])
        renderer = subprocess.list2cmdline(["Claude.exe", "--user-data-dir=" + directory, "--type=renderer"])
        with patch.object(app, "pids_por_nome", return_value=[1, 2, 3]), patch.object(
            app, "linha_de_comando", side_effect=[normal, renderer, "Claude.exe"]
        ):
            self.assertEqual(list(app._claudes_com_pasta()), [(1, directory)])

    def test_failed_close_is_reported(self):
        with patch.object(app, "claudes_abertos", return_value={"demo": 101}), patch.object(
            app.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)
        ), patch.object(app.time, "sleep"), patch.object(app, "log"):
            with self.assertRaises(OSError):
                app.fechar_claude("demo")

    def test_cleanup_stops_if_a_running_claude_cannot_be_inspected(self):
        with patch.object(app, "pids_por_nome", return_value={101}), patch.object(
            app, "linha_de_comando", return_value=""
        ), patch.object(app.subprocess, "run") as close:
            with self.assertRaises(OSError):
                app.fechar_claude("demo")
        close.assert_not_called()

    def test_finished_native_login_expires_pending_input(self):
        ui = Mock()
        ui.login_perfil = (1, "Test", "example", None)
        ui.calcular_estado.return_value = {"example": (True, True)}
        app.App.atualizar_estado(ui)
        ui.cancelar_login.assert_called_once()

    def test_empty_store_lookup_never_uses_an_executable_from_working_directory(self):
        with patch.object(app.subprocess, "check_output", return_value=""), patch.object(
            app.os.path, "exists", side_effect=lambda path: path == os.path.join("app", "claude.exe")
        ):
            self.assertIsNone(app.achar_claude())

    def test_legacy_invocation_does_not_open_a_database_supplied_target(self):
        with patch.object(sys, "argv", ["desktop", "--browser", "fictional-target"]), patch.object(
            app, "devolver_registros", create=True
        ) as restore, patch.object(app.os, "startfile") as launch, patch.object(
            app.ctypes.windll.user32, "MessageBoxW", return_value=1
        ):
            app.principal()
        restore.assert_not_called()
        launch.assert_not_called()


if __name__ == "__main__":
    unittest.main()
