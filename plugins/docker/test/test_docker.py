
import sys
import os
import json
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path
import io

# 7. Test file with sys.path.append (not sys.path.insert(0))
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'src')))
import plugin

class TestDockerComposePlugin(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.mock_home = Path(self.temp_dir.name)
        
    def tearDown(self):
        self.temp_dir.cleanup()

    @patch("sys.stdin", io.StringIO(""))
    def test_empty_stdin(self):
        with patch("sys.stdout", new_callable=io.StringIO) as fake_out:
            with self.assertRaises(SystemExit):
                plugin.main()
            self.assertIn("Empty stdin", fake_out.getvalue())

    @patch("plugin.get_config_path")
    def test_check_installed(self, mock_path):
        mock_path.return_value = self.mock_home / ".docker" / "config.json"
        req = {"requestId": "test-req", "action": "check_installed"}
        
        with patch("sys.stdin", io.StringIO(json.dumps(req))):
            with patch("sys.stdout", new_callable=io.StringIO) as fake_out:
                plugin.main()
                self.assertEqual(fake_out.getvalue().strip(), "true")

    @patch("plugin.get_config_path")
    def test_apply_settings(self, mock_path):
        config_file = self.mock_home / ".docker" / "config.json"
        mock_path.return_value = config_file
        
        # Setup existing mocked config
        config_file.parent.mkdir(parents=True, exist_ok=True)
        with open(config_file, "w", encoding="utf-8") as f:
            json.dump({"wslEngine": True, "compose": {"projectName": "old_name"}}, f)
            
        req = {
            "requestId": "req-123",
            "action": "apply",
            "args": {
                "settings": {
                    "experimental": True,
                    "projectName": "new_name",
                    "profiles": ["dev", "debug"]
                },
                "dryRun": False
            }
        }
        
        with patch("sys.stdin", io.StringIO(json.dumps(req))):
            with patch("sys.stdout", new_callable=io.StringIO) as fake_out:
                plugin.main()
                out = json.loads(fake_out.getvalue())
                
                # Check protocol constraints
                self.assertEqual(out.get("requestId"), "req-123")
                self.assertNotIn("success", out)
                self.assertNotIn("data", out)
                
        # Verify JSON state & Deep Merge behavior
        with open(config_file, "r", encoding="utf-8") as f:
            data = json.load(f)
            self.assertTrue(data["wslEngine"]) # Original non-compose key preserved
            self.assertEqual(data["compose"]["projectName"], "new_name")
            self.assertTrue(data["compose"]["experimental"])
            self.assertEqual(data["compose"]["profiles"], ["dev", "debug"])
        
        # Verify POSIX trailing newline
        with open(config_file, "rb") as f:
            content = f.read()
            self.assertTrue(content.endswith(b"\n"))

if __name__ == "__main__":
    unittest.main()


import json
import os
import sys
import tempfile
import unittest
from unittest.mock import patch

# Add src directory to path to import plugin
_src_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "../src"))
sys.path.append(_src_path)
import plugin

sys.path.remove(_src_path)


class TestDockerPlugin(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.config_path = os.path.join(self.temp_dir.name, "settings.json")

    def tearDown(self):
        self.temp_dir.cleanup()

    @patch("plugin.shutil.which")
    def test_check_installed(self, mock_which):
        mock_which.return_value = "C:\\Program Files\\Docker\\Docker\\resources\\bin\\docker.exe"

        response = plugin.check_installed({}, "req-1")

        self.assertTrue(response["success"])
        self.assertTrue(response["data"])
        mock_which.assert_called()

    def test_merge_settings(self):
        target = {
            "wslEngineEnabled": False,
            "proxies": {"httpProxy": "http://oldproxy:80"},
            "registryMirrors": ["https://old.mirror"],
        }

        source = {
            "wslEngineEnabled": True,
            "experimental": True,
            "proxies": {"httpsProxy": "http://newproxy:443"},
            "registryMirrors": ["https://new.mirror"],
        }

        changed = plugin.merge_settings(target, source)

        self.assertTrue(changed)
        self.assertTrue(target["wslEngineEnabled"])
        self.assertTrue(target["experimental"])

        # Nested dicts should merge
        self.assertEqual(target["proxies"]["httpProxy"], "http://oldproxy:80")
        self.assertEqual(target["proxies"]["httpsProxy"], "http://newproxy:443")

        # Arrays should overwrite
        self.assertEqual(target["registryMirrors"], ["https://new.mirror"])

    @patch("plugin.get_config_path")
    def test_apply_config_dry_run(self, mock_get_path):
        mock_get_path.return_value = self.config_path

        # Write initial config
        with open(self.config_path, "w", encoding="utf-8") as f:
            json.dump({"wslEngineEnabled": False}, f)

        args = {"settings": {"wslEngineEnabled": True}}

        # Dry run
        response = plugin.apply_config(args, {"dryRun": True}, "req-2")
        self.assertTrue(response["success"])
        self.assertTrue(response["changed"])

        # Verify file was NOT changed
        with open(self.config_path, "r", encoding="utf-8") as f:
            content = json.load(f)
        self.assertFalse(content["wslEngineEnabled"])

    @patch("plugin.get_config_path")
    def test_apply_config_real_run(self, mock_get_path):
        mock_get_path.return_value = self.config_path

        args = {"settings": {"kubernetes": {"enabled": True}}}

        # Real run on missing file (should create it)
        response = plugin.apply_config(args, {"dryRun": False}, "req-3")
        self.assertTrue(response["success"])
        self.assertTrue(response["changed"])

        # Verify file WAS created and changed
        with open(self.config_path, "r", encoding="utf-8") as f:
            content = json.load(f)
        self.assertTrue(content["kubernetes"]["enabled"])

    @patch("plugin.get_config_path")
    def test_apply_config_no_changes(self, mock_get_path):
        mock_get_path.return_value = self.config_path

        # Write initial config
        with open(self.config_path, "w", encoding="utf-8") as f:
            json.dump({"kubernetes": {"enabled": True}}, f)

        args = {"settings": {"kubernetes": {"enabled": True}}}

        # Real run but no actual differences
        response = plugin.apply_config(args, {"dryRun": False}, "req-4")
        self.assertTrue(response["success"])
        self.assertFalse(response["changed"])

    @patch("plugin.get_config_path")
    def test_read_corrupted_config(self, mock_get_path):
        mock_get_path.return_value = self.config_path

        # Write corrupted config
        with open(self.config_path, "w", encoding="utf-8") as f:
            f.write("{ invalid json")

        args = {"settings": {"wslEngineEnabled": True}}

        # Should back up corrupted and apply new
        response = plugin.apply_config(args, {"dryRun": False}, "req-5")
        self.assertTrue(response["success"])
        self.assertTrue(response["changed"])

        # Verify file WAS reset and written
        with open(self.config_path, "r", encoding="utf-8") as f:
            content = json.load(f)
        self.assertTrue(content["wslEngineEnabled"])

        # Verify backup was created
        dir_name = os.path.dirname(self.config_path)
        backups = [f for f in os.listdir(dir_name) if f.startswith("settings.json.corrupted.")]
        self.assertEqual(len(backups), 1)


if __name__ == "__main__":
    unittest.main()

