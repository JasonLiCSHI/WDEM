import importlib.util
import io
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

_PLUGIN_PATH = Path(__file__).parents[1] / "src" / "plugin.py"
_SPEC = importlib.util.spec_from_file_location("docker_compose_plugin", _PLUGIN_PATH)
if _SPEC is None or _SPEC.loader is None:
    raise RuntimeError(f"Could not load Docker Compose plugin from {_PLUGIN_PATH}")
plugin = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(plugin)


class TestDockerComposePlugin(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.config_path = Path(self.temp_dir.name) / ".docker" / "config.json"

    def tearDown(self):
        self.temp_dir.cleanup()

    def invoke_main(self, input_text):
        with (
            patch("sys.stdin", io.StringIO(input_text)),
            patch("sys.stdout", new_callable=io.StringIO) as stdout,
        ):
            plugin.main()
        return json.loads(stdout.getvalue())

    def test_empty_stdin_returns_json_error(self):
        response = self.invoke_main("")

        self.assertEqual("unknown", response["requestId"])
        self.assertIn("input", response["error"].lower())
        self.assertNotIn("success", response)
        self.assertNotIn("data", response)

    def test_invalid_json_returns_json_error(self):
        response = self.invoke_main("{not-json")

        self.assertEqual("unknown", response["requestId"])
        self.assertIn("invalid json", response["error"].lower())
        self.assertNotIn("success", response)
        self.assertNotIn("data", response)

    def test_check_installed_returns_bool_and_main_wraps_it(self):
        with patch.object(plugin.shutil, "which", return_value="/usr/bin/docker"):
            self.assertIs(plugin.check_installed(), True)
            response = self.invoke_main(json.dumps({"requestId": "req-1", "command": "check_installed"}))

        self.assertEqual({"requestId": "req-1", "installed": True}, response)

    def test_apply_deep_merges_compose_settings_from_args(self):
        self.config_path.parent.mkdir(parents=True)
        self.config_path.write_text(
            json.dumps(
                {
                    "auths": {"registry.example": {}},
                    "compose": {
                        "projectName": "old-name",
                        "nested": {"preserved": True},
                    },
                }
            ),
            encoding="utf-8",
        )
        request = {
            "requestId": "req-2",
            "command": "apply",
            "args": {
                "settings": {
                    "projectName": "new-name",
                    "nested": {"enabled": True},
                },
                "dryRun": False,
            },
            "context": {"dryRun": True},
        }

        with patch.object(plugin, "get_config_path", return_value=self.config_path):
            response = self.invoke_main(json.dumps(request))

        self.assertEqual({"requestId": "req-2", "changed": True}, response)
        config = json.loads(self.config_path.read_text(encoding="utf-8"))
        self.assertIn("registry.example", config["auths"])
        self.assertEqual("new-name", config["compose"]["projectName"])
        self.assertTrue(config["compose"]["nested"]["preserved"])
        self.assertTrue(config["compose"]["nested"]["enabled"])
        self.assertTrue(self.config_path.read_bytes().endswith(b"\n"))

    def test_dry_run_reports_change_without_writing(self):
        self.config_path.parent.mkdir(parents=True)
        self.config_path.write_text(
            json.dumps({"compose": {"projectName": "old-name"}}),
            encoding="utf-8",
        )
        before = self.config_path.read_bytes()

        with patch.object(plugin, "get_config_path", return_value=self.config_path):
            response = plugin.apply_config(
                {
                    "settings": {"projectName": "new-name"},
                    "dryRun": True,
                }
            )

        self.assertEqual({"changed": True}, response)
        self.assertEqual(before, self.config_path.read_bytes())

    def test_apply_uses_atomic_replace(self):
        with (
            patch.object(plugin, "get_config_path", return_value=self.config_path),
            patch.object(
                plugin.tempfile,
                "mkstemp",
                wraps=tempfile.mkstemp,
            ) as make_temporary,
            patch.object(plugin.os, "replace", wraps=os.replace) as replace,
        ):
            response = plugin.apply_config({"settings": {"projectName": "atomic"}, "dryRun": False})

        self.assertEqual({"changed": True}, response)
        make_temporary.assert_called_once()
        replace.assert_called_once()

    def test_missing_request_id_uses_unknown(self):
        response = self.invoke_main(json.dumps({"command": "unsupported"}))

        self.assertEqual("unknown", response["requestId"])
        self.assertIn("unsupported", response["error"])
        self.assertNotIn("success", response)
        self.assertNotIn("data", response)


if __name__ == "__main__":
    unittest.main()
