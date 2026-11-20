import importlib.machinery
import importlib.util
import os
from pathlib import Path
import sqlite3
import tempfile
import time
import unittest
from unittest.mock import Mock, patch


class ProfileLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temp = self.enterContext(tempfile.TemporaryDirectory())
        self.enterContext(patch.dict(os.environ, {"MULTI_CLAUDE_HOME": self.temp}))
        source = Path(__file__).resolve().parents[1] / "Multi Claude.pyw"
        loader = importlib.machinery.SourceFileLoader("launcher_test", str(source))
        self.app = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
        loader.exec_module(self.app)
        with self.app.db() as db:
            self.i = self.app.App._inserir(Mock(), db, "Test")
            self.key = self.app.prefs.all_settings(db)[self.i].key

    def test_shortcut_requires_exact_existing_catalog_and_profile(self):
        command = [self.key, "--manager-home", self.temp]
        self.assertEqual(self.app.shortcut_target(command), self.key)
        with self.assertRaises(ValueError):
            self.app.shortcut_target(["f" * 32, *command[1:]])
        other = Path(self.temp) / "absent"
        with self.assertRaises((OSError, ValueError, sqlite3.Error)):
            self.app.shortcut_target([self.key, "--manager-home", str(other)])
        self.assertFalse(other.exists())

    def test_deliver_once_and_ignore_expired_or_deleted_profiles(self):
        for when, key, expected in [(time.time() - 1000, self.key, None),
                                    (time.time(), "e"*32, None), (time.time(), self.key, self.i)]:
            self.app.queue_shortcut(key, when)
            self.assertEqual(self.app.next_shortcut_request(), expected)
            self.assertIsNone(self.app.next_shortcut_request())

    def test_reset_relaunch_keeps_the_home_selected_by_a_shortcut(self):
        with patch.object(self.app.subprocess, "Popen") as launch, self.assertRaises(SystemExit):
            self.app.reiniciar(Mock())
        self.assertEqual(launch.call_args.kwargs["env"]["MULTI_CLAUDE_HOME"], self.temp)
