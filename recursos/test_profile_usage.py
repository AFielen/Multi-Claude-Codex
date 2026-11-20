"""Synthetic Claude Code data, with no access to machine profiles."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import profile_usage as usage


class ProfileUsageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "claude-code" / ("ab" * 16)
        self.project = self.root / "projects" / "synthetic"
        self.project.mkdir(parents=True)

    def write_rows(self, filename, rows):
        path = self.project / filename
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"".join(json.dumps(row).encode() + b"\n" for row in rows))
        return path

    def test_real_counter_fields_deduplicated_across_forks_and_subagents(self):
        def reply(mid, out):
            return {"type": "assistant", "timestamp": "2026-11-04T12:00:00Z",
                    "message": {"id": mid, "usage": {"input_tokens": 19, "output_tokens": out,
                    "cache_creation_input_tokens": 4, "cache_read_input_tokens": 20},
                    "content": [{"text": "private fixture"}]}}
        self.write_rows("a.jsonl", [reply("msg-a", 1), reply("msg-a", 8), {"type":"user"}])
        self.write_rows("session/subagents/agent-b.jsonl", [reply("msg-b", 7), reply("msg-a", 8)])
        data = usage.read_usage(self.root)
        self.assertEqual((data.requests, data.input_tokens, data.output_tokens,
                          data.cache_write_tokens, data.cache_read_tokens, data.total_tokens),
                         (2, 38, 15, 8, 40, 101))
        self.assertTrue(data.available)
        self.assertNotIn("private fixture", repr(data))
        self.assertEqual(usage.read_usage(self.root, since=1900000000).requests, 0)

    def test_missing_or_unreadable_usage_is_not_an_invented_zero(self):
        self.assertFalse(usage.read_usage(self.root).available)
        (self.project / "bad.jsonl").write_bytes(b'broken\n{"type":"assistant","message":[]}')
        data = usage.read_usage(self.root)
        self.assertFalse(data.available)
        self.assertTrue(data.incomplete)

    def test_foreign_hardlinks_and_project_memory_are_never_counted(self):
        row = {"type": "assistant", "message": {"id": "other", "usage": {"input_tokens": 200}}}
        private = self.write_rows("memory/other.jsonl", [row])
        os.link(private, self.project / "linked.jsonl")
        self.write_rows("memory/custom.jsonl", [dict(row, message=dict(row["message"], id="memory"))])
        self.assertFalse(usage.read_usage(self.root).available)

    def test_unbound_paths_rejected(self):
        with self.assertRaises(ValueError):
            usage.set_limit_capture(self.root.parent / "unbound", True)

    def test_interrupted_writer_cannot_permanently_lock_preferences(self):
        (self.root / ".multiclaude-settings.lock").touch()
        self.assertTrue(usage.set_limit_capture(self.root, True))
        self.assertTrue(usage.limit_capture_enabled(self.root))

    def test_malformed_duplicate_settings_keys_are_preserved(self):
        settings = self.root / "settings.json"
        raw = b'{"hooks":{"first":1},"hooks":{"second":2}}'
        settings.write_bytes(raw)
        self.assertFalse(usage.set_limit_capture(self.root, True))
        self.assertEqual(settings.read_bytes(), raw)

    def test_foreign_hooks_preserved_and_owned_capture_removed_independently(self):
        settings = self.root / "settings.json"
        hooks = {"Notification": [{"hooks": [{"type": "command", "command": "fixture"}]}]}
        settings.write_text(json.dumps({"hooks": hooks}))
        self.assertTrue(usage.set_limit_capture(self.root, True))
        self.assertTrue(usage.limit_capture_enabled(self.root))
        self.assertEqual(json.loads(settings.read_text())["hooks"], hooks)
        self.assertTrue(usage.set_limit_capture(self.root, False))
        self.assertEqual(json.loads(settings.read_text()), {"hooks": hooks})
        foreign = b'{"statusLine":{"type":"command","command":"existing"},"other":false}'
        settings.write_bytes(foreign)
        self.assertFalse(usage.set_limit_capture(self.root, True))
        self.assertEqual(settings.read_bytes(), foreign)

    @unittest.skipUnless(os.name == "nt", "Windows status line")
    def test_real_powershell_status_line_writes_only_valid_windows(self):
        self.assertTrue(usage.set_limit_capture(self.root, True))
        command = json.loads((self.root / "settings.json").read_text())["statusLine"]["command"]
        now = int(time.time())
        payload = {"rate_limits": {"five_hour": {"used_percentage": 0, "resets_at": now + 300},
                                   "seven_day": {"used_percentage": 23.5, "resets_at": now + 600}},
                   "transcript_path": "private fixture", "session_id": "secret fixture"}
        context = dict(os.environ, CLAUDE_CONFIG_DIR=str(self.root))
        subprocess.run(command, input=json.dumps(payload), text=True, check=True,
                       capture_output=True, env=context, timeout=15)
        sample = usage.read_limits(self.root, now=now+1)
        self.assertEqual(sample["five_hour"], {"used_percentage": 0, "resets_at": now + 300})
        self.assertEqual(sample["seven_day"]["used_percentage"], 23.5)
        self.assertLessEqual(abs(sample["observed_at"] - now), 10)
        self.assertNotIn("five_hour", usage.read_limits(self.root, now=now+301))
        self.assertEqual(usage.read_limits(self.root, now=now+601), {})
        payload["rate_limits"]["five_hour"]["used_percentage"] = 105
        payload["rate_limits"]["seven_day"]["used_percentage"] = 31
        subprocess.run(command, input=json.dumps(payload), text=True, check=True,
                       capture_output=True, env=context, timeout=15)
        updated = usage.read_limits(self.root)
        self.assertNotIn("five_hour", updated)
        self.assertEqual(updated["seven_day"]["used_percentage"], 31)
        for path in self.root.iterdir():
            if path.is_file():
                self.assertNotIn(b"private fixture", path.read_bytes())
                self.assertNotIn(b"secret fixture", path.read_bytes())
        self.assertTrue(usage.set_limit_capture(self.root, False))
        subprocess.run(command, input=json.dumps(payload), text=True, check=True,
                       capture_output=True, env=context, timeout=15)
        self.assertEqual(usage.read_limits(self.root), {})


if __name__ == "__main__":
    unittest.main()
