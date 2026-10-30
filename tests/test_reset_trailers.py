import hashlib
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts' / 'reset_trailers.py'
spec = importlib.util.spec_from_file_location('reset_trailers', SCRIPT)
reset = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reset)


class ResetTrailerTests(unittest.TestCase):
    def test_preview_and_delete_are_scoped_to_trailers_and_matching_backups(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            trailers = root / 'trailers'
            trailers.mkdir()
            video = trailers / 'Example.mp4'
            video.write_bytes(b'video')
            sidecar = video.with_suffix('.json')
            sidecar.write_text('{}')
            unrelated = trailers / 'unrelated.json'
            unrelated.write_text('{}')
            child = trailers / 'subfolder'
            child.mkdir()
            nested = child / 'keep.mp4'
            nested.write_bytes(b'keep')
            link = trailers / 'linked.mp4'
            link.symlink_to(nested)
            plugin_data = root / 'plugin-data'
            key = hashlib.sha256(str(video).encode()).hexdigest().upper()
            backups = []
            for folder, suffix in [('trailer-audio-originals', '.mp4'), ('trailer-audio-originals-state', '.json')]:
                directory = plugin_data / folder
                directory.mkdir(parents=True)
                path = directory / (key + suffix)
                path.write_text('backup')
                backups.append(path)
            args = [sys.executable, str(SCRIPT), '--folder', str(trailers), '--plugin-data', str(plugin_data)]
            preview = subprocess.run(args, capture_output=True, text=True)
            self.assertEqual(preview.returncode, 0, preview.stderr)
            self.assertIn('PREVIEW ONLY', preview.stdout)
            self.assertTrue(all(p.exists() for p in [video, sidecar, *backups]))
            deletion = subprocess.run(args + ['--delete'], capture_output=True, text=True)
            self.assertEqual(deletion.returncode, 0, deletion.stderr)
            self.assertTrue(all(not p.exists() for p in [video, sidecar, *backups]))
            self.assertTrue(all(p.exists() for p in [unrelated, nested, link]))

    def test_rejects_filesystem_root(self):
        with self.assertRaises(ValueError):
            reset.cleanup_files(Path('/'))
