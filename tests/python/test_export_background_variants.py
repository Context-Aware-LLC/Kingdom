import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

from tooling.scripts import export_background_variants as exporter


class BackgroundExportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.output = self.root / "variants"
        self.manifest = {"backgrounds": [{"key": "Hall", "source": "hall.png"}]}
        Image.new("RGB", (160, 90), (90, 70, 40)).save(self.root / "hall.png")

    def run_export(self, **kwargs):
        with contextlib.redirect_stdout(io.StringIO()):
            return exporter.export(self.root, self.manifest, self.output, **kwargs)

    def test_exports_both_exact_sizes_without_touching_source_or_existing_metadata(self):
        original = (self.root / "hall.png").read_bytes()
        meta = self.output / "DesktopTablet/Hall.png.meta"
        meta.parent.mkdir(parents=True)
        meta.write_text("guid: keep-this-guid\n", encoding="utf-8")
        self.run_export()
        for tier, size in exporter.TIERS.items():
            with Image.open(self.output / tier / "Hall.png") as image:
                self.assertEqual(size, image.size)
        self.assertEqual(original, (self.root / "hall.png").read_bytes())
        self.assertEqual("guid: keep-this-guid\n", meta.read_text(encoding="utf-8"))
        manifest = json.loads((self.output / "export-manifest.json").read_text())
        self.assertEqual(2, len(manifest["backgrounds"][0]["outputs"]))

    def test_bad_aspect_anywhere_prevents_all_writes(self):
        Image.new("RGB", (1425, 820)).save(self.root / "login.png")
        self.manifest["backgrounds"].append({"key": "Login", "source": "login.png"})
        with self.assertRaisesRegex(ValueError, "not exact 16:9"):
            self.run_export()
        self.assertFalse(self.output.exists())

    def test_explicit_crop_retains_only_the_selected_region(self):
        image = Image.new("RGB", (164, 94), "red")
        image.paste("blue", (2, 2, 162, 92))
        image.save(self.root / "hall.png")
        self.manifest["backgrounds"][0]["crop"] = [2, 2, 162, 92]
        with patch.object(exporter, "TIERS", {"SmallTest": (32, 18)}):
            self.run_export()
        with Image.open(self.output / "SmallTest/Hall.png") as output:
            self.assertEqual(((0, 0), (0, 0), (255, 255)), output.getextrema())

    def test_repeated_export_does_not_rewrite_identical_files(self):
        with patch.object(exporter, "TIERS", {"SmallTest": (32, 18)}):
            self.run_export()
            path = self.output / "SmallTest/Hall.png"
            timestamp = path.stat().st_mtime_ns
            self.run_export()
            self.assertEqual(timestamp, path.stat().st_mtime_ns)

    def test_dry_run_does_not_create_folders(self):
        self.run_export(dry_run=True)
        self.assertFalse(self.output.exists())

    def test_source_cannot_be_an_output(self):
        self.output = self.root
        target = self.root / "Mobile/Hall.png"
        target.parent.mkdir()
        (self.root / "hall.png").rename(target)
        self.manifest["backgrounds"][0]["source"] = "Mobile/Hall.png"
        with self.assertRaisesRegex(ValueError, "overwrite a source"):
            self.run_export()

    def test_duplicate_keys_are_rejected_case_insensitively(self):
        self.manifest["backgrounds"].append({"key": "hall", "source": "hall.png"})
        with self.assertRaisesRegex(ValueError, "duplicate"):
            self.run_export()

    def test_crop_outside_source_is_rejected(self):
        self.manifest["backgrounds"][0]["crop"] = [0, 0, 320, 180]
        with self.assertRaisesRegex(ValueError, "invalid crop"):
            self.run_export()

    def test_unknown_selection_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unknown background"):
            self.run_export(only=["Typo"])

    def test_reexported_source_requires_review_of_old_crop_coordinates(self):
        self.manifest["backgrounds"][0]["expected_source_size"] = [320, 180]
        with self.assertRaisesRegex(ValueError, "source changed"):
            self.run_export()

    def test_path_cannot_escape_project(self):
        self.manifest["backgrounds"][0]["source"] = "../outside.png"
        with self.assertRaisesRegex(ValueError, "inside the project"):
            self.run_export()


if __name__ == "__main__":
    unittest.main()
