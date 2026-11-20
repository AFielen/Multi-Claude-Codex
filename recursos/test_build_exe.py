"""Exercise the packaging boundary without building or starting the desktop app."""
import os
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
PORTRAITS = (
    "astronaut.png", "carp.png", "comet.png", "cosmic-cat.png", "dragon.png",
    "fox.png", "ghosty.png", "owl.png", "pot.png", "robot.png",
)
EXE = "Multi Claude.exe"


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        runtime = self.root / "python"
        runtime.mkdir()
        (runtime / "LICENSE.txt").write_bytes(b"Fixture Python notice\r\nSecond line")
        self.enterContext(patch.object(sys, "base_prefix", str(runtime)))
        self.enterContext(patch.object(sys, "path", [str(runtime), *sys.path]))
        for name, license_file in (("pyinstaller", "COPYING.txt"), ("pillow", "LICENSE")):
            metadata = runtime / f"{name}-1.0.dist-info"
            licenses = metadata / "licenses"
            licenses.mkdir(parents=True)
            (metadata / "METADATA").write_text(f"Name: {name}\nVersion: 1.0\n", encoding="utf8")
            (metadata / "RECORD").write_text(f"{metadata.name}/licenses/{license_file},,\n", encoding="utf8")
            (licenses / license_file).write_bytes(f"Fixture {name} ©\r\nSecond line".encode())
        self.assets = [Path("recursos/icone.ico"), Path("recursos/icone.png"),
                       Path("LICENSE"), Path("NOTICE"), Path("AUTHORS")]
        self.assets.extend(Path("profile_pictures", name) for name in PORTRAITS)
        script = Path("recursos/build_exe.py")
        self.modules = [Path(name + ".py") for name in ("profile_code", "profile_settings", "profile_shortcut",
                        "profile_switcher", "profile_text", "profile_translations", "profile_tray", "profile_usage",
                        "windows_profiles", "workspace_usage")]
        for path in [*self.assets, *self.modules, script]:
            target = self.root / path
            target.parent.mkdir(exist_ok=True)
            shutil.copyfile(ROOT / path, target)

        (self.root / "Multi Claude.pyw").write_text(
            'VERSAO = "1.2.3"\nraise AssertionError("app was executed")\n', encoding="utf8"
        )
        self.build = runpy.run_path(str(self.root / script))["main"]

    def test_only_reviewed_assets_reach_freezer_and_notices_ship_with_exe(self):
        for path in ("profile_pictures/family.png", "profile_pictures/passwords.txt",
                     "profile_pictures/usuario_demo.png", "perfis.db", "log.txt"):
            (self.root / path).write_bytes(b"private test fixture")
        original_icon = self.root / "recursos/icone.ico"
        os.utime(original_icon, (1_000_000_000, 1_000_000_000))
        initial_mtime = original_icon.stat().st_mtime_ns
        build_sentinel = self.root / "build/keep.txt"
        build_sentinel.parent.mkdir()
        build_sentinel.write_bytes(b"existing build output")
        bundled = {}

        def inspect_freezer_inputs(args, **kwargs):
            self.assertEqual(set(path.name for path in Path(kwargs["cwd"]).glob("*.py")),
                             {path.name for path in self.modules})
            for index, arg in enumerate(args):
                if arg != "--add-data":
                    continue
                src, dest = args[index + 1].rsplit(":", 1)
                src = Path(src)
                if src.is_dir():
                    for child in src.rglob("*"):
                        if child.is_file():
                            bundled[Path(dest, child.relative_to(src))] = child.read_bytes()
                else:
                    bundled[Path(dest, src.name)] = src.read_bytes()
            output = Path(args[args.index("--distpath") + 1])
            output.mkdir(parents=True, exist_ok=True)
            (output / EXE).write_bytes(b"synthetic packaged output")

        with patch("subprocess.run", side_effect=inspect_freezer_inputs):
            self.build()

        self.assertEqual(set(bundled), set(self.assets))
        for path in self.assets:
            self.assertEqual(bundled[path], (ROOT / path).read_bytes(), path)
        self.assertEqual((self.root / "dist" / EXE).read_bytes(), b"synthetic packaged output")
        for name in ("NOTICE", "LICENSE", "AUTHORS"):
            self.assertEqual((self.root / "dist" / name).read_bytes(), (ROOT / name).read_bytes())
        third_party = (self.root / "dist/THIRD-PARTY-NOTICES.txt").read_bytes()
        for expected in (b"Fixture Python notice\r\nSecond line",
                         "Fixture pyinstaller ©\r\nSecond line".encode(),
                         "Fixture pillow ©\r\nSecond line".encode()):
            self.assertIn(expected, third_party)
        self.assertEqual(initial_mtime, original_icon.stat().st_mtime_ns)
        self.assertEqual(build_sentinel.read_bytes(), b"existing build output")

    def test_replaced_default_aborts_before_freezing(self):
        (self.root / "profile_pictures/fox.png").write_bytes(b"substituted personal photo")
        with patch("subprocess.run") as freeze:
            with self.assertRaisesRegex(ValueError, r"fox\.png"):
                self.build()
            freeze.assert_not_called()

    def test_freezer_failure_preserves_previous_release(self):
        previous = self.root / "dist" / EXE
        previous.parent.mkdir()
        previous.write_bytes(b"previous release")

        def fail_midway(args, **kwargs):
            dist = Path(args[args.index("--distpath") + 1])
            dist.mkdir(parents=True, exist_ok=True)
            (dist / EXE).write_bytes(b"incomplete executable")
            raise subprocess.CalledProcessError(1, args)

        with patch("subprocess.run", side_effect=fail_midway):
            with self.assertRaises(subprocess.CalledProcessError):
                self.build()
        self.assertEqual(previous.read_bytes(), b"previous release")


if __name__ == "__main__":
    unittest.main()
