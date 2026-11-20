"""Exercise quota delivery and expiry while a fictional transcript scan is blocked."""
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from preview_workspace import fictional_workspace, load_application


class WorkspaceQuotaTests(unittest.TestCase):
    def test_disabling_capture_invalidates_a_pending_refresh_immediately(self):
        with fictional_workspace(96, 2) as app:
            app.withdraw()
            source = load_application()
            profile = app.perfis()[0]
            key = app.options[profile[0]].key
            old_reading = app.limits[key]
            app.refresh_usage(profile)
            pending = app.usage_revision
            app.scanning = True
            app.scan_queue.put((pending, None, None, False, {key: old_reading}, None))
            with (patch.object(source.profile_code, "desktop_environment"),
                  patch.object(source.profile_usage, "set_limit_capture", return_value=True)):
                app.toggle_capture(profile, False)
            self.assertGreater(app.usage_revision, pending)
            self.assertFalse(app.limits.get(key))
            self.assertFalse(app.usage_cache[key][2])
            self.assertFalse(app.usage_view.body.winfo_manager())
            app.poll_usage()
            self.assertFalse(app.limits.get(key))
            after_change = app.usage_revision
            app.refresh_usage(profile)
            self.assertEqual(app.usage_revision, after_change)

    def test_quotas_switch_and_expire_before_the_transcript_finishes(self):
        with fictional_workspace(96, 2) as app:
            app.withdraw()
            source = load_application()
            first, second = app.perfis()
            keys = [app.options[row[0]].key for row in (first, second)]
            now = int(time.time())
            quotas = {
                key: {"observed_at": now, "five_hour": {"used_percentage": percent, "resets_at": now + 30}}
                for key, percent in zip(keys, (34, 83))
            }
            tokens = source.profile_usage.TokenUsage(input_tokens=11, available=True)
            started, release = threading.Event(), threading.Event()

            def slow_tokens(path):
                started.set()
                if not release.wait(5):
                    raise TimeoutError("Fixture transcript did not resume")
                return tokens

            app.usage_cache.clear()
            app.limits.clear()
            app.scanning = True
            app.refresh_key = keys[0]
            with (patch.object(source.profile_code, "code_config_dir", side_effect=lambda base, key: key),
                  patch.object(source.profile_usage, "read_limits", side_effect=quotas.get),
                  patch.object(source.profile_usage, "read_usage", side_effect=slow_tokens),
                  patch.object(source.profile_usage, "limit_capture_enabled", return_value=True),
                  ThreadPoolExecutor(max_workers=1) as executor):
                worker = executor.submit(source.App.scan_usage, "", keys[0], keys, app.scan_queue, app.usage_revision, None)
                try:
                    self.assertTrue(started.wait(2))
                    app.poll_usage()
                    self.assertEqual(app.usage_view.metrics["five_hour"][0]["text"], "34 %")
                    self.assertEqual(app.usage_view.body.winfo_manager(), "pack")
                    self.assertFalse(app.usage_cache)
                    self.assertTrue(app.scanning)
                    self.assertEqual(app.refresh_key, keys[0])
                    app.select_profile(second[0])
                    self.assertEqual(app.usage_view.metrics["five_hour"][0]["text"], "83 %")
                    with patch.object(source.time, "time", return_value=now + 31):
                        app.poll_usage()
                        self.assertFalse(app.usage_view.body.winfo_manager())
                finally:
                    release.set()
                worker.result(timeout=2)
                app.select_profile(first[0])
                app.poll_usage()
                self.assertEqual(app.usage_cache[keys[0]][1], tokens)
                self.assertIsNone(app.refresh_key)
