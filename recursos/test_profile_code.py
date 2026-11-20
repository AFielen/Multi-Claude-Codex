"""Isolated tests for the native Claude Code launcher, with no real sign-in or CLI execution."""
import os
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from uuid import uuid4

import profile_code as code


class CodeProfiles(unittest.TestCase):
    system_shell = Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.base = self.root / "Portable O'Brien $1"
        self.base.mkdir()
        self.user = self.root / "user"
        self.user.mkdir()
        self.env = {"USERPROFILE": str(self.user), "LOCALAPPDATA": str(self.user / "local"),
                    "APPDATA": str(self.user / "roaming"), "SYSTEMROOT": str(self.root / "Windows")}
        self.enterContext(patch.dict(os.environ, self.env, clear=True))
        self.key = uuid4().hex

    def test_credentials_and_history_have_independent_stable_roots(self):
        other = uuid4().hex
        with patch.dict(os.environ, {"ANTHROPIC_API_KEY": "fixture", "CLAUDE_CODE_OAUTH_TOKEN": "fixture",
                                    "CLAUDE_CODE_USE_BEDROCK": "1", "ANTHROPIC_PROFILE": "other",
                                    "ANTHROPIC_CONFIG_DIR": "old-profile", "LANG": "de_DE",
                                    "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1"}):
            first = code.desktop_environment(self.base, self.key)
            second = code.desktop_environment(self.base, other)
            self.assertNotEqual(first["CLAUDE_CONFIG_DIR"], second["CLAUDE_CONFIG_DIR"])
            self.assertNotEqual(first["ANTHROPIC_CONFIG_DIR"], second["ANTHROPIC_CONFIG_DIR"])
            self.assertEqual(Path(first["CLAUDE_CONFIG_DIR"]), code.code_config_dir(self.base, self.key))
            self.assertTrue(Path(first["ANTHROPIC_CONFIG_DIR"]).is_dir())
            self.assertEqual(first["LANG"], "de_DE")
            self.assertNotIn("ANTHROPIC_API_KEY", first)
            self.assertNotIn("CLAUDE_CODE_OAUTH_TOKEN", first)
            self.assertNotIn("ANTHROPIC_PROFILE", first)
            self.assertNotIn("CLAUDE_CODE_USE_BEDROCK", first)
            self.assertEqual(first["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"], "1")
            self.assertEqual(os.environ["ANTHROPIC_API_KEY"], "fixture")
            self.assertEqual(os.environ["ANTHROPIC_CONFIG_DIR"], "old-profile")

    def test_profile_keys_cannot_reuse_rowids_or_escape_base(self):
        for key in (1, "1", "../one", "x" * 32, None):
            with self.subTest(key=key), self.assertRaises(ValueError):
                code.code_config_dir(self.base, key)

    def test_only_recognized_cli_locations_are_considered(self):
        desktop = self.user / "local" / "AnthropicClaude"
        desktop.mkdir(parents=True)
        (desktop / "claude.exe").touch()
        with patch.dict(os.environ, {"PATH": str(desktop)}), patch.object(code, "_signed_by_anthropic") as verify:
            self.assertIsNone(code.find_claude_code())
            verify.assert_not_called()
        cli = self.user / ".local" / "bin" / "claude.exe"
        cli.parent.mkdir(parents=True)
        cli.touch()
        with patch.object(code, "_signed_by_anthropic", return_value=False):
            self.assertIsNone(code.find_claude_code())
        with patch.object(code, "_signed_by_anthropic", return_value=True):
            self.assertEqual(code.find_claude_code(), cli)

    def test_package_manager_layouts(self):
        candidates = [
            self.user / "local/Microsoft/WinGet/Packages/Anthropic.ClaudeCode_Microsoft.Winget.Source/claude.exe",
            self.user / "roaming/npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe",
            self.user / "roaming/npm/node_modules/@anthropic-ai/claude-code-win32-x64/claude.exe",
            self.user / "roaming/npm/node_modules/@anthropic-ai/claude-code/node_modules/@anthropic-ai/claude-code-win32-x64/claude.exe",
        ]
        for cli in candidates:
            with self.subTest(cli=cli), patch.object(code, "_signed_by_anthropic", return_value=True), patch.object(
                code.platform, "machine", return_value="AMD64"
            ):
                cli.parent.mkdir(parents=True, exist_ok=True)
                cli.touch()
                self.assertEqual(code.find_claude_code(), cli)
                cli.unlink()

    def test_winps_signature_check_is_independent_of_powershell7_modules(self):
        candidate = self.root / "fixture.exe"
        candidate.touch()
        with patch.dict(os.environ, {"PSMODULEPATH": str(self.root / "foreign-modules")}), patch.object(
            code.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)
        ) as run:
            self.assertTrue(code._signed_by_anthropic(candidate))
        args, options = run.call_args
        self.assertEqual(args[0][0], str(code._powershell()))
        self.assertEqual(options["env"]["PSMODULEPATH"], str(code._powershell().parent / "Modules"))
        self.assertEqual(options["env"]["MULTI_CLAUDE_SIGNATURE_PATH"], str(candidate))

    def test_copy_command_rechecks_profile_and_does_not_edit_shell_environment(self):
        copied = code.powershell_command(self.base, self.key, login=True)
        self.assertIn("--code-terminal", copied)
        self.assertIn("--manager-home", copied)
        self.assertIn("--login", copied)
        self.assertIn(self.key, copied)
        self.assertNotIn("CLAUDE_CONFIG_DIR", copied)
        shared = code.powershell_command(self.base, None)
        self.assertIn("shared", shared)
        self.assertNotIn("--login", shared)
        with self.assertRaises(ValueError):
            code.powershell_command(self.base, None, login=True)

    def test_powershell_preserves_quoted_arguments_current_directory_and_exit_code(self):
        source = self.root / "O’Brien's $fixture & [test]"
        source.mkdir()
        app = source / "Multi Claude.pyw"
        app.write_text("import json,os,sys\nfrom pathlib import Path\n"
                       "Path('result.json').write_text(json.dumps([sys.argv[1:],os.getcwd()]))\n"
                       "sys.exit(7)\n", encoding="utf8")
        with patch.object(code, "__file__", str(source / "profile_code.py")):
            copied = code.powershell_command(self.base, self.key)
        env = dict(os.environ, SYSTEMROOT=str(self.system_shell.parents[3]), PATHEXT=".COM;.EXE;.BAT;.CMD")
        result = subprocess.run([str(self.system_shell), "-NoProfile", "-NonInteractive", "-Command",
                                 copied + "; exit $LASTEXITCODE"], cwd=self.root, env=env,
                                capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(result.returncode, 7, result.stderr)
        seen, cwd = json.loads((self.root / "result.json").read_text())
        self.assertEqual(seen, ["--code-terminal", self.key, "--manager-home", str(self.base)])
        self.assertEqual(Path(cwd), self.root)

    def catalog(self):
        db = sqlite3.connect(self.base / "perfis.db")
        try:
            db.execute("CREATE TABLE perfis(id INTEGER PRIMARY KEY)")
            db.execute("CREATE TABLE profile_options(profile INTEGER, code_key TEXT, isolated INTEGER, sharing INTEGER DEFAULT 0)")
            db.execute("INSERT INTO perfis VALUES (1)")
            db.execute("INSERT INTO profile_options(profile, code_key, isolated) VALUES (1, ?, 1)", (self.key,))
            db.commit()
        finally:
            db.close()

    def test_pasted_terminal_requires_current_isolated_catalog_profile(self):
        self.catalog()
        with patch.object(code, "_launch_spec") as select:
            select.return_value = ([sys.executable, "-c", "raise SystemExit(7)"], None)
            self.assertEqual(code.run_terminal_command(self.root, [self.key, "--manager-home", str(self.base)]), 7)
            select.assert_called_once_with(self.base, self.key, False)
            select.reset_mock()
            with self.assertRaises(ValueError):
                code.run_terminal_command(self.base, [uuid4().hex])
            db = sqlite3.connect(self.base / "perfis.db")
            db.execute("UPDATE profile_options SET isolated=0")
            db.commit()
            db.close()
            with self.assertRaises(ValueError):
                code.run_terminal_command(self.base, [self.key])
            select.assert_not_called()

    def test_terminal_starts_interactively_in_child_environment(self):
        with patch.object(code, "find_claude_code", return_value=self.user / "claude.exe"), patch.object(
            subprocess, "Popen"
        ) as launch:
            code.open_terminal(self.base, self.key, login=True)
        args, options = launch.call_args
        self.assertEqual(options["creationflags"], subprocess.CREATE_NEW_CONSOLE)
        self.assertEqual(options["env"]["CLAUDE_CONFIG_DIR"], str(code.code_config_dir(self.base, self.key)))
        self.assertEqual(args[0][-2:], ["auth", "login"])

    def test_existing_profile_resources_are_preserved(self):
        shared = self.user / ".claude"
        shared.mkdir()
        (shared / "CLAUDE.md").write_text("shared", encoding="utf8")
        env = code.desktop_environment(self.base, self.key)
        local = Path(env["CLAUDE_CONFIG_DIR"]) / "CLAUDE.md"
        local.write_text("profile-specific", encoding="utf8")
        code.share_resources(self.base, self.key, True)
        code.share_resources(self.base, self.key, False)
        self.assertEqual(local.read_text(encoding="utf8"), "profile-specific")
        self.assertEqual((shared / "CLAUDE.md").read_text(encoding="utf8"), "shared")

    def test_user_storage_is_never_copied_when_sharing(self):
        shared = self.user / ".claude"
        shared.mkdir()
        (shared / ".credentials.json").write_text("synthetic fixture", encoding="utf8")
        (shared / "settings.json").write_text("{}", encoding="utf8")
        self.assertEqual(code.share_resources(self.base, self.key, True), ())
        config = code.code_config_dir(self.base, self.key)
        self.assertFalse((config / ".credentials.json").exists())
        self.assertFalse((config / "settings.json").exists())

    def test_reversible_links_and_changed_local_contents(self):
        shared = self.user / ".claude"
        (shared / "skills").mkdir(parents=True)
        (shared / "CLAUDE.md").write_text("shared rules", encoding="utf8")
        config = code.code_config_dir(self.base, self.key)
        linked = code.share_resources(self.base, self.key, True)
        self.assertEqual(set(linked), {"skills", "CLAUDE.md"})
        self.assertEqual((config / "skills").resolve(), (shared / "skills").resolve())
        (config / "CLAUDE.md").unlink()
        (config / "CLAUDE.md").write_text("local rules", encoding="utf8")
        code.share_resources(self.base, self.key, False)
        self.assertFalse(os.path.lexists(config / "skills"))
        self.assertEqual((config / "CLAUDE.md").read_text(encoding="utf8"), "local rules")
        self.assertEqual((shared / "CLAUDE.md").read_text(encoding="utf8"), "shared rules")

    def test_failed_disable_restores_manager_links_and_manifest(self):
        shared = self.user / ".claude"
        (shared / "skills").mkdir(parents=True)
        (shared / "CLAUDE.md").write_text("shared", encoding="utf8")
        config = code.code_config_dir(self.base, self.key)
        manifest = config / ".multi-claude-shared.json"
        original_replace = Path.replace
        for replaced in (False, True):
            with self.subTest(private_replacement=replaced):
                code.share_resources(self.base, self.key, True)
                before = json.loads(manifest.read_text())
                failures = []

                def fail_once(tmp, destination):
                    if destination == manifest and not failures:
                        failures.append(True)
                        if replaced:
                            (config / "CLAUDE.md").write_text("private", encoding="utf8")
                        raise OSError("sharing manifest unavailable")
                    return original_replace(tmp, destination)

                with patch.object(Path, "replace", fail_once), self.assertRaises(OSError):
                    code.share_resources(self.base, self.key, False)
                self.assertEqual((config / "skills").resolve(), (shared / "skills").resolve())
                if replaced:
                    self.assertEqual((config / "CLAUDE.md").read_text(), "private")
                    del before["CLAUDE.md"]
                else:
                    self.assertEqual((config / "CLAUDE.md").read_bytes(), code._MEMORY_IMPORT)
                self.assertEqual(json.loads(manifest.read_text()), before)

    def test_saved_command_mounts_newly_available_global_resources(self):
        self.catalog()
        with sqlite3.connect(self.base / "perfis.db") as db:
            db.execute("UPDATE profile_options SET sharing=1")
        db.close()
        shared = self.user / ".claude" / "agents"
        shared.mkdir(parents=True)
        with patch.object(code, "_launch_spec", return_value=([sys.executable, "-c", "pass"], None)):
            self.assertEqual(code.run_terminal_command(self.base, [self.key]), 0)
        self.assertEqual((code.code_config_dir(self.base, self.key) / "agents").resolve(), shared.resolve())

    def test_stale_terminal_refresh_cannot_undo_editor_disable(self):
        self.catalog()
        with sqlite3.connect(self.base / "perfis.db") as db:
            db.execute("UPDATE profile_options SET sharing=1")
        db.close()
        code.share_resources(self.base, self.key, False)
        (self.user / ".claude" / "skills").mkdir(parents=True)
        with patch.object(code, "_launch_spec", return_value=([sys.executable, "-c", "pass"], None)):
            code.run_terminal_command(self.base, [self.key])
        self.assertFalse((code.code_config_dir(self.base, self.key) / "skills").exists())
