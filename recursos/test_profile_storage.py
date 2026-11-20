"""Windows profile-storage tests. All paths and profiles are temporary fixtures."""
import contextlib
import importlib.machinery
import importlib.util
import os
from pathlib import Path
import sqlite3
import stat
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch


SOURCE = Path(__file__).resolve().parents[1] / "Multi Claude.pyw"


class ProfileStorageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.base = self.root / "portable"
        env = {"MULTI_CLAUDE_HOME": str(self.base), "USERPROFILE": str(self.root),
               "LOCALAPPDATA": str(self.root / "local"), "APPDATA": str(self.root / "roaming")}
        self.enterContext(patch.dict(os.environ, env))
        loader = importlib.machinery.SourceFileLoader("profile_storage_fixture", str(SOURCE))
        self.mod = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
        loader.exec_module(self.mod)
        self.app = Mock(spec=self.mod.App)
        self.app.perfis.side_effect = lambda: self.mod.App.perfis(self.app)
        self.app.t.side_effect = lambda key, **kw: self.mod.T["en"][key].format(**kw)
        self.app.confirmar.return_value = True
        self.app.login_perfil = None
        self.close = self.enterContext(patch.object(self.mod, "fechar_claude", return_value=True))
        self.dialog = self.enterContext(patch.object(self.mod, "dialogo", return_value="DELETE"))
        self.restart = self.enterContext(patch.object(self.mod, "reiniciar"))

    def profile(self, rel, name=None):
        folder = self.root / rel
        folder.mkdir(parents=True, exist_ok=True)
        for marker in ("config.json", "Local State"):
            (folder / marker).write_text("{}", encoding="utf-8")
        with self.mod.db() as con:
            self.mod.App._inserir(self.app, con, name or folder.name, str(folder))
        return self.app.perfis()[-1]

    def test_single_delete_unlinks_external_reference(self):
        profile = self.profile("other/app/dados/linked")
        with patch.object(self.mod.shutil, "rmtree") as erase:
            self.mod.App.excluir(self.app, profile)
        erase.assert_not_called()
        self.close.assert_not_called()
        self.assertEqual(self.app.perfis(), [])

    def test_mixed_catalog_stays_available_to_the_windows_edition(self):
        claude = self.profile("portable/dados/claude")
        foreign = self.profile("portable/foreign", name="Codex")
        with self.mod.db() as con:
            con.execute("ALTER TABLE profile_options ADD COLUMN provider TEXT DEFAULT 'claude'")
            con.execute("UPDATE profile_options SET provider='codex' WHERE profile=?", (foreign[0],))
            options = self.mod.prefs.all_settings(con)
            claude_key, codex_key = options[claude[0]].key, options[foreign[0]].key
            pinned = self.root / "original Claude home"
            con.execute("INSERT INTO config VALUES (?,?)", ("wpf_code_home_" + claude_key, str(pinned)))
        with self.assertRaisesRegex(OSError, "WPF"):
            self.app.perfis()
        self.mod.App.apagar_tudo(self.app)
        self.restart.assert_not_called()
        self.assertTrue(Path(claude[2], "Local State").is_file())
        self.assertTrue(Path(foreign[2], "Local State").is_file())
        self.assertEqual(self.mod.shortcut_target([claude_key, "--manager-home", str(self.base)]), claude_key)
        with self.assertRaisesRegex(OSError, "WPF"):
            self.mod.shortcut_target([codex_key, "--manager-home", str(self.base)])
        with self.assertRaisesRegex(OSError, "WPF"):
            self.mod.queue_shortcut(claude_key, time.time())
        self.mod.queue_shortcut(claude_key, time.time(), already_running=True)
        with self.assertRaisesRegex(OSError, "WPF"):
            self.mod.queue_shortcut(codex_key, time.time(), already_running=True)
        with self.assertRaisesRegex(OSError, "WPF"):
            self.mod.profile_code.run_terminal_command(self.base, [codex_key])
        with (patch.object(self.mod.profile_code, "_launch_spec", return_value=(["fixture"], {})) as select,
              patch.object(self.mod.profile_code.subprocess, "run") as run):
            run.return_value.returncode = 0
            self.assertEqual(self.mod.profile_code.run_terminal_command(self.base, [claude_key]), 0)
            select.assert_called_once_with(pinned, claude_key, False)
        with contextlib.closing(sqlite3.connect(self.mod.DB)) as con:
            self.assertEqual(con.execute("SELECT COUNT(*) FROM perfis").fetchone(), (2,))
            self.assertEqual(con.execute("SELECT code_key FROM open_requests").fetchall(), [(claude_key,)])
        original = self.mod.DB
        self.app._inserir.side_effect = lambda *args: self.mod.App._inserir(self.app, *args)
        self.app.nome_livre.side_effect = lambda *args: self.mod.App.nome_livre(self.app, *args)
        with patch.object(self.mod, "DB", str(self.root / "destination.db")):
            self.assertEqual(self.mod.importar_banco(self.app, original), 1)
            self.assertEqual([p[2] for p in self.app.perfis()], [claude[2]])

    def test_codex_folders_are_never_discovered_as_claude(self):
        for name in (".codex", "roaming/Codex", "local/Packages/OpenAI.Codex_abc/LocalCache",
                     "other/codex-desktop/" + "a" * 32, "other/codex-home/" + "b" * 32):
            folder = self.root / name
            folder.mkdir(parents=True)
            for marker in ("config.json", "Local State"):
                (folder / marker).write_text("{}")
            self.assertFalse(self.mod.eh_perfil_claude(str(folder)), name)

    def test_shared_managed_directory_is_only_unlinked(self):
        profile = self.profile("portable/dados/shared")
        self.profile("portable/dados/shared", name="second")
        with patch.object(self.mod.shutil, "rmtree") as erase:
            self.mod.App.excluir(self.app, profile)
        erase.assert_not_called()
        self.assertEqual(len(self.app.perfis()), 1)

    def test_failed_delete_keeps_registration_for_retry(self):
        profile = self.profile("portable/dados/person")
        with patch.object(self.mod.shutil, "rmtree", side_effect=PermissionError("busy")):
            self.mod.App.excluir(self.app, profile)
        self.assertEqual(self.app.perfis(), [profile])
        self.dialog.assert_called()
        self.mod.App.excluir(self.app, profile)
        self.assertFalse(Path(profile[2]).exists())
        self.assertEqual(self.app.perfis(), [])

    def test_partial_delete_retries_same_directory_without_markers(self):
        profile = self.profile("portable/dados/partially-removed")
        def partial(folder):
            Path(folder, "config.json").unlink()
            raise PermissionError("busy")
        with patch.object(self.mod.shutil, "rmtree", side_effect=partial):
            self.mod.App.excluir(self.app, profile)
        self.assertEqual(self.app.perfis(), [profile])
        self.mod.App.excluir(self.app, profile)
        self.assertFalse(Path(profile[2]).exists())

    def test_reparse_data_root_is_preserved_on_reset(self):
        profile = self.profile("portable/dados/child")
        original = os.lstat
        def lstat(path, *args, **kwargs):
            info = original(path, *args, **kwargs)
            if os.path.normcase(str(path)) == os.path.normcase(self.mod.DADOS):
                return SimpleNamespace(st_mode=info.st_mode, st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT)
            return info
        with patch.object(os, "lstat", side_effect=lstat), patch.object(self.mod.shutil, "rmtree") as erase:
            self.mod.App.apagar_tudo(self.app)
        erase.assert_not_called()
        self.assertTrue(Path(profile[2], "config.json").is_file())

    def test_legacy_local_alias_protects_the_managed_target(self):
        owned = self.profile("portable/dados/shared")
        alias = self.profile("old/mount/shared", "alias")
        mount = str(Path(alias[2]).parent)
        original = os.lstat
        def lstat(path, *args, **kwargs):
            info = original(path, *args, **kwargs)
            if os.path.normcase(str(path)) == os.path.normcase(mount):
                return SimpleNamespace(st_mode=info.st_mode, st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT)
            return info
        with (patch.object(os, "lstat", side_effect=lstat), patch.object(os, "readlink", return_value=self.mod.DADOS),
              patch.object(self.mod.shutil, "rmtree") as erase):
            self.mod.App.excluir(self.app, owned)
        erase.assert_not_called()
        self.close.assert_not_called()

    def test_legacy_absolute_path_is_unlinked(self):
        profile = self.profile("portable/dados/absolute")
        with self.mod.db() as con:
            con.execute("UPDATE perfis SET pasta = ? WHERE id = ?", (profile[2], profile[0]))
        self.app.confirmar.side_effect = [True, False]
        self.mod.App.excluir(self.app, profile)
        self.assertEqual(self.app.perfis(), [])
        self.assertTrue(Path(profile[2]).exists())

    def test_delete_cancels_pending_signin(self):
        profile = self.profile("portable/dados/person")
        self.app.login_perfil = profile
        self.mod.App.excluir(self.app, profile)
        self.app.cancelar_login.assert_called_once()

    def test_reset_preserves_linked_and_unregistered_data(self):
        linked = self.profile("another/app/dados/linked")
        owned = self.profile("portable/dados/owned")
        kept = self.base / "dados" / "previously-unlinked"
        kept.mkdir()
        (kept / "keep.txt").write_text("keep", encoding="utf-8")
        self.mod.App.apagar_tudo(self.app)
        self.assertTrue(Path(linked[2], "config.json").is_file())
        self.assertTrue((kept / "keep.txt").is_file())
        self.assertFalse(Path(owned[2]).exists())
        self.assertFalse(Path(self.mod.DB).exists())
        self.restart.assert_called_once()

    def test_failed_reset_keeps_metadata(self):
        profile = self.profile("portable/dados/owned")
        with patch.object(self.mod.shutil, "rmtree", side_effect=PermissionError("busy")):
            self.mod.App.apagar_tudo(self.app)
        self.assertEqual(self.app.perfis(), [profile])
        self.restart.assert_not_called()

    def test_changed_folder_without_profile_markers_is_preserved(self):
        profile = self.profile("portable/dados/repurposed")
        Path(profile[2], "config.json").unlink()
        with patch.object(self.mod.shutil, "rmtree") as erase:
            self.mod.App.excluir(self.app, profile)
        erase.assert_not_called()

    def test_main_package_profile_cannot_be_imported(self):
        root = Path(os.environ["LOCALAPPDATA"]) / "Packages" / "Claude_Example" / "LocalCache" / "Claude"
        root.mkdir(parents=True)
        for marker in ("Local State", "config.json"):
            (root / marker).write_text("{}", encoding="utf-8")
        self.assertFalse(self.mod.eh_perfil_claude(str(root)))

    def test_db_import_skips_invalid_rows_and_external_avatar(self):
        original = self.profile("source/profiles/one")
        with self.mod.db() as con:
            con.execute("DELETE FROM perfis")
        source = self.root / "source" / "profiles #2.db"
        photos = Path(self.mod.FOTOS)
        photos.mkdir()
        outside = self.root / "selected.png"
        outside.write_bytes(b"unused")
        con = sqlite3.connect(source)
        try:
            con.execute("CREATE TABLE perfis(nome, pasta, foto)")
            con.executemany("INSERT INTO perfis VALUES (?, ?, ?)", [
                (None, "foo", None), ("invalid", 9, None),
                ("one", "profiles/one", str(outside)), ("duplicate", "profiles/one", None),
            ])
            con.commit()
        finally:
            con.close()
        self.app._inserir.side_effect = lambda *args: self.mod.App._inserir(self.app, *args)
        self.app.nome_livre.side_effect = lambda *args: self.mod.App.nome_livre(self.app, *args)
        self.assertEqual(self.mod.importar_banco(self.app, str(source)), 1)
        self.assertEqual(self.app.perfis()[0][1:], ("one", original[2], None))

    def test_open_allows_new_managed_and_existing_linked_profiles(self):
        linked = self.profile("other/dados/one")
        for folder in (str(self.base / "dados" / "new"), linked[2]):
            self.mod.validar_pasta_para_abrir(folder)
        with self.assertRaises(OSError):
            self.mod.validar_pasta_para_abrir(str(self.root / "other" / "missing"))
        with self.assertRaises(OSError):
            self.mod.validar_pasta_para_abrir(str(self.base))

    def test_new_names_fit_windows_directory_components(self):
        with self.mod.db() as con:
            for name in ("NUL", "a" * 300):
                self.mod.App._inserir(self.app, con, name)
        for _, name, folder, _ in self.app.perfis():
            self.assertLessEqual(len(Path(folder).name), 100)
            self.assertNotEqual(Path(folder).name.upper(), "NUL")
            self.assertEqual(Path(folder).parent, Path(self.mod.DADOS))

    def test_redirected_gallery_does_not_read_or_write_images(self):
        image = Mock()
        with patch.object(self.mod, "caminho_sem_desvios", return_value=False), patch.object(self.mod, "Image", image):
            self.mod.preparar_fotos()
            self.assertEqual(self.mod.listar_fotos(), [])
            with self.assertRaises(OSError):
                self.mod.importar_foto("unused.png")
        image.open.assert_not_called()
        self.assertFalse(Path(self.mod.FOTOS).exists())

    def test_nonlocal_and_ambiguous_entries_are_rejected_without_lookup(self):
        invalid = [r"\\example.invalid\share\profile", r"\\?\C:\profile", r"\\.\device",
                   r"C:relative", r"\relative-to-current-drive", "", None, b"binary"]
        with patch.object(os, "lstat") as lstat, patch.object(os.path, "realpath") as resolve:
            for candidate in invalid:
                self.assertEqual(self.mod.caminho_real(candidate), "")
                self.assertFalse(self.mod.caminho_sem_desvios(candidate))
                self.assertFalse(self.mod.identidade_pasta(candidate))
                self.assertFalse(self.mod.eh_perfil_claude(candidate))
            lstat.assert_not_called()
            resolve.assert_not_called()

    def test_malformed_legacy_entry_can_be_unlinked(self):
        with self.mod.db() as con:
            con.execute("INSERT INTO perfis (nome, pasta) VALUES (?, ?)", ("legacy", b"bad"))
        profile = self.app.perfis()[0]
        with patch.object(self.mod.shutil, "rmtree") as erase:
            self.mod.App.excluir(self.app, profile)
        erase.assert_not_called()
        self.close.assert_not_called()
        self.assertEqual(self.app.perfis(), [])


if __name__ == "__main__":
    unittest.main()
