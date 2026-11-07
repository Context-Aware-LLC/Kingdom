import hashlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / "tooling/scripts/publish_background_content.py"
SPEC = importlib.util.spec_from_file_location("publish_background_content", SCRIPT)
publisher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(publisher)


class BackgroundPublicationTests(unittest.TestCase):
    def setUp(self):
        output = patch("sys.stdout", new=io.StringIO())
        output.start()
        self.addCleanup(output.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = self.root / "background-content.json"
        self.manifest = {
            "version": 1, "platform": "Android", "bucket": "kobi-game-assets",
            "keyPrefix": "backgrounds/v1/Android",
            "baseUrl": publisher.ORIGIN + "backgrounds/v1/Android", "bundles": [],
        }
        for i in range(13):
            name = f"backgrounds_desktoptablet_assets_test{i}_{i:032x}.bundle"
            content = f"bundle {i}".encode()
            (self.root / name).write_bytes(content)
            self.manifest["bundles"].append({
                "file": name, "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest(),
            })
        self.save()

    def save(self):
        self.path.write_text(json.dumps(self.manifest), encoding="utf-8")

    def test_accepts_exact_build_and_refuses_changed_files_before_upload(self):
        self.assertEqual(len(publisher.load_manifest(self.path)["bundles"]), 13)
        (self.root / self.manifest["bundles"][-1]["file"]).write_bytes(b"changed")
        with patch.object(publisher, "upload") as upload, self.assertRaises(ValueError):
            publisher.publish(self.path, allow_upload=True)
        upload.assert_not_called()

    def test_refuses_path_traversal_and_wrong_destination(self):
        self.manifest["bundles"][0]["file"] = "../escape.bundle"
        self.save()
        with self.assertRaises(ValueError):
            publisher.load_manifest(self.path)
        self.manifest["baseUrl"] = "https://other.example.com"
        self.save()
        with self.assertRaises(ValueError):
            publisher.load_manifest(self.path)

    def test_partial_publication_never_writes_a_receipt(self):
        with patch.object(publisher, "remote_matches", side_effect=[True] * 12 + [False]):
            with self.assertRaises(ValueError):
                publisher.publish(self.path)
        self.assertFalse(Path(str(self.path) + ".published.json").exists())

    def test_uploads_only_missing_objects_and_verifies_them_before_receipt(self):
        with patch.object(publisher, "remote_matches", side_effect=[False, True] + [True] * 12), \
                patch.object(publisher, "upload") as upload:
            receipt = publisher.publish(self.path, allow_upload=True)
        self.assertEqual(upload.call_count, 1)
        self.assertEqual(json.loads(receipt.read_text())["manifestSha256"], publisher.digest(self.path))

    def test_wrong_public_bytes_are_not_overwritten(self):
        with patch.object(publisher.urllib.request, "urlopen", return_value=io.BytesIO(b"wrong")):
            with self.assertRaisesRegex(ValueError, "refusing to overwrite"):
                publisher.remote_matches("https://example.com/bundle", self.manifest["bundles"][0])


if __name__ == "__main__":
    unittest.main()
