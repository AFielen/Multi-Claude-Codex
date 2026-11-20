import hashlib
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import time
import unittest
from unittest.mock import Mock, patch
import _winapi

import prepare_portable_update as update


class ProcessCheckTests(unittest.TestCase):
    def test_only_the_selected_managers_mutex_blocks_a_check(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp)
            kernel = Mock()
            kernel.OpenMutexW.return_value = 0
            with (patch.object(update.ctypes, "WinDLL", return_value=kernel),
                  patch.object(update.ctypes, "get_last_error", return_value=2) as last_error,
                  patch.object(update.subprocess, "run", side_effect=AssertionError("No global process inventory"))):
                update._require_idle(source)
                (source / "claude-code").mkdir()
                update._require_idle(source)
                kernel.OpenMutexW.return_value = 123
                with self.assertRaisesRegex(RuntimeError, "still running"):
                    update._require_idle(source)
                kernel.OpenMutexW.return_value = 0
                last_error.return_value = 5
                with self.assertRaisesRegex(RuntimeError, "verify whether"):
                    update._require_idle(source)


class UpdateTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.release = self.root / "release"
        self.release.mkdir()
        self.target = self.root / "Permanent"
        self.source = self.root / "zip-temp" / "old app"
        self.source.mkdir(parents=True)
        self.enterContext(patch.object(update, "_temporary_roots", return_value=[self.source.parent]))
        self.idle = self.enterContext(patch.object(update, "_require_idle"))
        for name in update.PACKAGE:
            (self.release / name).write_bytes(("release " + name).encode())
        db = sqlite3.connect(self.source / "perfis.db")
        db.execute("CREATE TABLE perfis(id INTEGER PRIMARY KEY, nome TEXT, pasta TEXT)")
        db.execute("INSERT INTO perfis VALUES (1,'demo','dados/demo')")
        db.commit()
        db.close()
        (self.source / "dados" / "demo").mkdir(parents=True)

    def test_stages_only_release_files_and_keeps_original_basis(self):
        (self.release / "AUTHORS").write_bytes(b"Edition contributor credits")
        (self.release / "perfis.db").write_bytes(b"unrelated fixture")
        original = (self.source / "perfis.db").read_bytes()
        exe_hash = hashlib.sha256((self.release / "Multi Claude.exe").read_bytes()).hexdigest()
        update.stage(self.release, self.target, exe_hash)
        self.assertEqual(set(p.name for p in (self.target / "App").iterdir()), {*update.PACKAGE, "AUTHORS"})
        self.assertEqual((self.target / "App" / "AUTHORS").read_bytes(), b"Edition contributor credits")
        self.assertFalse((self.target / "Profiles").exists())
        launcher = (self.target / update.LAUNCHER).read_text(encoding="ascii")
        self.assertIn("MULTI_CLAUDE_HOME=%~dp0Profiles", launcher)
        self.assertIn("perfis.db", launcher)
        self.assertEqual(update.check(self.source, self.target), self.target / "Profiles")
        self.assertEqual((self.source / "perfis.db").read_bytes(), original)
        self.assertFalse((self.source / "perfis.db-shm").exists())
        self.idle.assert_called()

    def test_stops_on_nonempty_destination_or_changed_package(self):
        self.target.mkdir()
        marker = self.target / "existing.txt"
        marker.write_text("keep")
        with self.assertRaises(ValueError):
            update.stage(self.release, self.target)
        self.assertEqual(marker.read_text(), "keep")
        with self.assertRaises(ValueError):
            update.stage(self.release, self.root / "other", "0" * 64)
        self.assertFalse((self.root / "other" / "App").exists())

    def test_check_requires_closed_processes_and_no_active_catalog_journal(self):
        update.stage(self.release, self.target)
        self.idle.side_effect = RuntimeError("running")
        with self.assertRaisesRegex(RuntimeError, "running"):
            update.check(self.source, self.target)
        self.idle.side_effect = None
        (self.source / "perfis.db-wal").write_bytes(b"uncheckpointed fixture")
        with self.assertRaisesRegex(ValueError, "SQLite"):
            update.check(self.source, self.target)

    def test_internal_absolute_catalog_reference_requires_individual_migration(self):
        update.stage(self.release, self.target)
        con = sqlite3.connect(self.source / "perfis.db")
        con.execute("UPDATE perfis SET pasta=?", (str(self.source / "dados" / "demo"),))
        con.commit()
        con.close()
        with self.assertRaisesRegex(ValueError, "absolut"):
            update.check(self.source, self.target)

    def test_checks_junction_targets_without_entering_linked_resources(self):
        update.stage(self.release, self.target)
        external = self.root / "external"
        external.mkdir()
        nested = external / "marker"
        nested.write_text("keep")
        junction = self.source / "dados" / "demo" / "shared"
        _winapi.CreateJunction(str(external), str(junction))
        update.check(self.source, self.target)
        self.assertEqual(nested.read_text(), "keep")
        junction.rmdir()
        _winapi.CreateJunction(str(self.source / "dados"), str(junction))
        with self.assertRaisesRegex(ValueError, "Link"):
            update.check(self.source, self.target)

    def test_never_recommends_explorer_merge_or_starts_without_catalog(self):
        update.stage(self.release, self.target)
        (self.target / self.source.name).mkdir()
        with self.assertRaisesRegex(ValueError, "same name"):
            update.check(self.source, self.target)
        failed = subprocess.run(["cmd.exe", "/d", "/c", update.LAUNCHER], cwd=self.target, input=b"\n",
                                capture_output=True, timeout=10)
        self.assertEqual(failed.returncode, 1)
        self.assertIn(b"perfis.db", failed.stdout)
        self.assertFalse((self.target / "Profiles").exists())

    def test_complete_fixture_move_keeps_ids_junction_and_catalog(self):
        update.stage(self.release, self.target)
        external = self.root / "external"
        external.mkdir()
        link = self.source / "dados" / "demo" / "skills"
        _winapi.CreateJunction(str(external), str(link))
        link_identity = link.lstat().st_ino
        db_identity = (self.source / "perfis.db").stat().st_ino
        profiles = update.check(self.source, self.target)
        # Simulate Explorer's same-volume directory move on this disposable fixture.
        self.source.rename(profiles)
        self.assertEqual((profiles / "perfis.db").stat().st_ino, db_identity)
        moved_link = profiles / "dados" / "demo" / "skills"
        self.assertEqual(moved_link.lstat().st_ino, link_identity)
        self.assertTrue(moved_link.samefile(external))
        catalog = sqlite3.connect((profiles / "perfis.db").as_uri() + "?mode=ro&immutable=1", uri=True)
        try:
            relative = catalog.execute("SELECT pasta FROM perfis").fetchone()[0]
            self.assertTrue((profiles / relative).is_dir())
        finally:
            catalog.close()

    def test_access_probe_only_blocks_handles_in_this_store(self):
        update.stage(self.release, self.target)
        fixture = self.source / "dados" / "demo" / "busy"
        fixture.write_text("fixture")
        outside = self.root / "elsewhere"
        outside.write_text("other session")
        with outside.open("rb"), fixture.open("rb") as busy:
            with self.assertRaisesRegex(RuntimeError, "handle is in use"):
                update.check(self.source, self.target)
            busy.close()
            self.assertEqual(update.check(self.source, self.target), self.target / "Profiles")

    def test_interim_starter_runs_only_the_verified_release_and_original_catalog(self):
        compiler = Path(os.environ["SystemRoot"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        if not compiler.is_file():
            self.skipTest("Windows .NET Framework test compiler is unavailable")
        fixture = self.root / "StarterFixture.cs"
        fixture.write_text('using System; using System.IO; class Test {static void Main(){'
                           'File.WriteAllText(Environment.GetEnvironmentVariable("MC_TEST_RESULT"),'
                           'Environment.GetEnvironmentVariable("MULTI_CLAUDE_HOME"));}}', encoding="ascii")
        app = self.release / update.PACKAGE[0]
        subprocess.run([str(compiler), "/nologo", "/target:winexe", "/out:" + str(app), str(fixture)],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=True)
        # A literal %, exclamation mark, ampersand, apostrophe and non-ASCII must survive double-click.
        special = self.source.with_name("demo %PATH% & ! ' ä")
        self.source.rename(special)
        self.target = self.root / "Release %PATH% & ! ' ä"
        sha = update.stage(self.release, self.target)
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            update.bridge(special, self.target, "0" * 64)
        starter = update.bridge(special, self.target, sha)
        self.assertEqual(update.bridge(special, self.target, sha), starter)
        marker = self.root / "started.txt"
        def click():
            return subprocess.run(["cmd.exe", "/d", "/c", starter.name], cwd=self.target, input=b"\n",
                                  env=dict(os.environ, MC_TEST_RESULT=str(marker)), capture_output=True, timeout=15)
        result = click()
        self.assertEqual(result.returncode, 0, result.stderr)
        for _ in range(60):
            if marker.is_file() and marker.stat().st_size:
                break
            time.sleep(.05)
        self.assertEqual(marker.read_text(encoding="utf-8"), str(special))
        marker.unlink()
        catalog = special / "perfis.db"
        held = special / "original"
        catalog.rename(held)
        self.assertEqual(click().returncode, 1)
        catalog.write_bytes(held.read_bytes())  # Same content on a different NTFS file.
        self.assertEqual(click().returncode, 1)
        catalog.unlink()
        held.rename(catalog)
        (self.target / "App" / update.PACKAGE[0]).write_bytes(b"replacement")
        self.assertEqual(click().returncode, 1)
        self.assertFalse(marker.exists())
