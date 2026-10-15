import sys
import unittest
from pathlib import Path

import numpy as np

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT))

from tooling.scripts import ship_spell_effect as ship  # noqa: E402


class DefringeTests(unittest.TestCase):
    def sheet(self):
        # A solid orange disc ringed by faint neon red, on transparent black.
        a = np.zeros((40, 40, 4), dtype=np.uint8)
        y, x = np.ogrid[:40, :40]
        r = np.hypot(y - 20, x - 20)
        a[r < 10] = (240, 140, 20, 255)
        ring = (r >= 10) & (r < 13)
        a[ring] = (255, 0, 0, 20)
        a[(r >= 13) & (r < 14)] = (255, 0, 0, 3)
        return a, ring

    def test_faint_neon_takes_the_solid_colour_and_keeps_its_alpha(self):
        a, ring = self.sheet()
        out = ship.defringe(a)
        self.assertTrue((out[ring][:, :3] == (240, 140, 20)).all())
        self.assertTrue((out[ring][:, 3] == 20).all())

    def test_near_invisible_texels_are_cleared(self):
        a, _ = self.sheet()
        out = ship.defringe(a)
        self.assertEqual(0, int(((a[..., 3] == 3) & (out[..., 3] != 0)).sum()))

    def test_solid_texels_are_untouched(self):
        a, _ = self.sheet()
        out = ship.defringe(a)
        solid = a[..., 3] == 255
        self.assertTrue((out[solid] == a[solid]).all())


class GroupEntryTests(unittest.TestCase):
    def group(self, *guids):
        lines = ["  m_SerializeEntries:"]
        for g in guids:
            lines += [f"  - m_GUID: {g}", "    m_Address: ui/x", "    m_ReadOnly: 0",
                      "    m_SerializedLabels: []", "    FlaggedDuringContentUpdateRestriction: 0"]
        lines.append("  m_ReadOnly: 0")
        return "\n".join(lines) + "\n"

    def test_inserts_in_guid_order(self):
        low, mid, high = "1" * 32, "5" * 32, "9" * 32
        out = ship.add_group_entry(self.group(low, high), mid, "ui/effect_new")
        self.assertLess(out.index(low), out.index(mid))
        self.assertLess(out.index(mid), out.index(high))
        self.assertIn("    m_Address: ui/effect_new\n", out)

    def test_a_guid_after_every_entry_goes_last_and_inside_the_list(self):
        low, last = "1" * 32, "f" * 32
        out = ship.add_group_entry(self.group(low), last, "ui/effect_new")
        self.assertLess(out.index(last), out.index("\n  m_ReadOnly: 0\n"))


if __name__ == "__main__":
    unittest.main()
