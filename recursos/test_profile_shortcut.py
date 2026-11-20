"""Windows Shell Link integration using temporary profile catalogs and output directories."""
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from uuid import uuid4
import zlib

import profile_shortcut as shortcut


class ProfileShortcutTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.base = self.root / "Managed profiles' $1"
        self.base.mkdir()
        self.key = uuid4().hex
        self.link = self.root / "Work & personal.lnk"

    def properties(self):
        script = ("[Console]::OutputEncoding=[Text.Encoding]::UTF8;"
                  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($env:TEST_PROFILE_LINK);"
                  "[Console]::Write((@($s.TargetPath,$s.Arguments,$s.IconLocation,$s.Description)|ConvertTo-Json -Compress))")
        shell = shortcut._powershell()
        env = dict(os.environ, TEST_PROFILE_LINK=str(self.link), PSMODULEPATH=str(shell.parent / "Modules"))
        result = subprocess.run([str(shell), "-NoProfile", "-NonInteractive", "-Command", script],
                                env=env, capture_output=True, check=True, timeout=30,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        return json.loads(result.stdout)

    def test_real_link_uses_exact_program_and_stable_profile_uuid(self):
        shortcut.save_shortcut(self.link, self.base, self.key, "Demo", "#1c725f")
        target, args, icon, description = self.properties()
        program = Path(sys.executable).with_name("pythonw.exe")
        self.assertEqual(Path(target), program if program.is_file() else Path(sys.executable))
        self.assertIn("--open-profile " + self.key, args)
        self.assertIn(subprocess.list2cmdline(["--manager-home", str(self.base)]), args)
        self.assertIn("Demo", description)
        icon_path = Path(icon.rsplit(",", 1)[0])
        self.assertEqual(icon_path.parent, self.base / "profile_shortcuts")
        blob = icon_path.read_bytes()
        self.assertEqual(struct.unpack_from("<HHH", blob), (0, 1, 7))
        self.assertTrue(blob[6 + 16 * 7:].startswith(b"\x89PNG"))
        shortcut.save_shortcut(self.link, self.base, self.key, "Changed", "#bd301f")
        _, _, updated_icon, updated_name = self.properties()
        self.assertNotEqual(updated_icon, icon)
        self.assertIn("Changed", updated_name)
        self.assertTrue(icon_path.is_file())

    def test_failure_preserves_preexisting_shortcut(self):
        self.link.write_bytes(b"old link")
        with patch.object(shortcut.subprocess, "run", side_effect=OSError("busy")), self.assertRaises(OSError):
            shortcut.save_shortcut(self.link, self.base, self.key, "Demo", "#123456")
        self.assertEqual(self.link.read_bytes(), b"old link")

    def test_frozen_profile_link_with_portrait(self):
        # A valid, synthetic one-pixel RGBA portrait; the shell reads only this fixture.
        def chunk(kind, value):
            return struct.pack(">I", len(value)) + kind + value + struct.pack(">I", zlib.crc32(kind + value))

        photos = self.base / "profile_pictures"
        photos.mkdir()
        portrait = photos / "red.png"
        portrait.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
                             + chunk(b"IDAT", zlib.compress(b"\0\xff\0\0\xff")) + chunk(b"IEND", b""))
        exe = self.root / "Multi Claude.exe"
        exe.touch()
        with patch.object(sys, "frozen", True, create=True), patch.object(sys, "executable", str(exe)):
            shortcut.save_shortcut(self.link, self.base, self.key, "Work", "#114488", portrait)
        target, args, icon, _ = self.properties()
        self.assertEqual(Path(target), exe)
        self.assertEqual(args, subprocess.list2cmdline(["--open-profile", self.key, "--manager-home", str(self.base)]))
        self.assertTrue(Path(icon.rsplit(",", 1)[0]).is_file())

    def test_refuses_unrelated_files_and_invalid_inputs(self):
        for destination, key, color in [(self.root / "document.txt", self.key, "#112233"),
                                        (self.link, "../escape", "#112233"), (self.link, self.key, "red")]:
            with self.subTest(destination=destination, key=key), self.assertRaises(ValueError):
                shortcut.save_shortcut(destination, self.base, key, "Demo", color)
        self.assertFalse(self.link.exists())
