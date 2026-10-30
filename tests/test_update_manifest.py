"""Exercise manifest generation without modifying the repository catalogue."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import yaml

SCRIPT = Path(__file__).resolve().parents[1] / "update_manifest.py"


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.original = [{"versions": [{"version": "1.0.18.6", "targetAbi": "10.11.0.0"}]}]
        (self.root / "manifest.json").write_text(json.dumps(self.original))
        # This file represents the tagged metadata, separate from main's build.yaml.
        (self.root / "release.yaml").write_text(yaml.safe_dump({
            "version": "2.0.0.0", "targetAbi": "12.0.0.0", "changelog": ["Jellyfin 12 support"]
        }))
        (self.root / "build.yaml").write_text("version: 99.0.0.0\ntargetAbi: 99.0.0.0\n")

    def run_update(self, version="2.0.0.0", checksum="a" * 32):
        return subprocess.run([
            sys.executable, str(SCRIPT), version, checksum,
            "--build-file", "release.yaml", "--repository", "example/Trailers4Jellyfin",
        ], cwd=self.root, capture_output=True, text=True)

    def read_manifest(self):
        return json.loads((self.root / "manifest.json").read_text())

    def test_release_uses_tagged_metadata_and_fork(self):
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stderr)
        entries = self.read_manifest()[0]["versions"]
        self.assertEqual(entries[1], self.original[0]["versions"][0])
        self.assertEqual(entries[0]["targetAbi"], "12.0.0.0")
        self.assertEqual(entries[0]["sourceUrl"],
                         "https://github.com/example/Trailers4Jellyfin/releases/download/v2.0.0.0/trailers4jellyfin_2.0.0.0.zip")
        self.assertEqual(entries[0]["changelog"], "- Jellyfin 12 support\n")

    def test_repeat_is_idempotent(self):
        self.assertEqual(self.run_update().returncode, 0)
        before = (self.root / "manifest.json").read_bytes()
        self.assertEqual(self.run_update().returncode, 0)
        self.assertEqual(before, (self.root / "manifest.json").read_bytes())

    def test_rejects_changed_artifact_for_existing_version(self):
        self.assertEqual(self.run_update().returncode, 0)
        self.assertNotEqual(self.run_update(checksum="b" * 32).returncode, 0)

    def test_rejects_wrong_tag_and_invalid_checksum(self):
        for version, checksum in [("2.0.1.0", "a" * 32), ("2.0.0.0", "placeholder")]:
            with self.subTest(version=version, checksum=checksum):
                self.assertNotEqual(self.run_update(version, checksum).returncode, 0)
                self.assertEqual(self.read_manifest(), self.original)

    def test_out_of_order_release_stays_sorted(self):
        data = self.read_manifest()
        data[0]["versions"].insert(0, {"version": "2.0.1.0"})
        (self.root / "manifest.json").write_text(json.dumps(data))
        self.assertEqual(self.run_update().returncode, 0)
        self.assertEqual([v["version"] for v in self.read_manifest()[0]["versions"]],
                         ["2.0.1.0", "2.0.0.0", "1.0.18.6"])


if __name__ == "__main__":
    unittest.main()
