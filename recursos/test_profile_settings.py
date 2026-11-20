import sqlite3
import unittest
from contextlib import closing

import profile_settings as prefs


class ProfileSettingsTests(unittest.TestCase):
    def setUp(self):
        self.con = self.enterContext(closing(sqlite3.connect(":memory:")))
        self.con.execute("PRAGMA foreign_keys=ON")
        self.con.execute("CREATE TABLE perfis(id INTEGER PRIMARY KEY, nome TEXT UNIQUE)")
        self.con.executemany("INSERT INTO perfis VALUES (?,?)", [(1, "Work"), (2, "Home")])
        prefs.migrate(self.con)

    def test_upgrade_preserves_order_and_previous_code_mode(self):
        old = prefs.all_settings(self.con)
        prefs.migrate(self.con)
        self.assertEqual(old, prefs.all_settings(self.con))
        self.assertEqual(list(old), [1, 2])
        self.assertFalse(old[1].isolated)
        self.assertNotEqual(old[1].key, old[2].key)

    def test_new_profile_reusing_rowid_never_inherits_a_code_account(self):
        old = prefs.all_settings(self.con)[2]
        self.con.execute("DELETE FROM perfis WHERE id=2")
        self.con.execute("INSERT INTO perfis(nome) VALUES ('New')")
        prefs.add(self.con, 2)
        new = prefs.all_settings(self.con)[2]
        self.assertNotEqual(old.key, new.key)
        self.assertTrue(new.isolated)
        self.assertNotEqual(prefs.all_settings(self.con)[1].color, new.color)

    def test_move_preserves_other_profiles_and_stable_shortcuts(self):
        self.con.execute("INSERT INTO perfis(nome) VALUES ('Third')")
        prefs.add(self.con, 3)
        old = prefs.all_settings(self.con)
        prefs.move(self.con, 3, 1)
        prefs.migrate(self.con)
        new = prefs.all_settings(self.con)
        self.assertEqual(list(new), [3, 1, 2])
        self.assertEqual(new[3].shortcut, old[3].shortcut)
