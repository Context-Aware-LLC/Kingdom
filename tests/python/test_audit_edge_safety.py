import contextlib
import copy
import io
import os
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import patch


REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT))

from tooling.scripts import audit_edge_safety as audit  # noqa: E402


class EdgeSafetyTests(unittest.TestCase):
    def setUp(self):
        self.previous_cwd = Path.cwd()
        os.chdir(REPO_ROOT)
        self.addCleanup(os.chdir, self.previous_cwd)

    def test_live_screens_pass_all_edge_audit_passes(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = audit.main()
        self.assertEqual(0, result, output.getvalue())

    def test_declared_parent_chains_exist_and_carry_clearance(self):
        for cls, relative in audit.PARENT_CLEARANCE:
            with self.subTest(cls=cls, screen=relative):
                self.assertTrue(audit._has_parent_clearance(str(Path(audit.UI) / relative), cls))
        self.assertFalse(audit._has_parent_clearance(
            str(Path(audit.UI) / 'Collection/CollectionScreen.uxml'), 'col-back-corner'))

    def _check_mutated_chain(self, mutation):
        for (cls, relative), (chain, _) in audit.PARENT_CLEARANCE.items():
            with self.subTest(cls=cls, screen=relative), tempfile.TemporaryDirectory() as directory:
                root = ET.parse(Path(audit.UI) / relative).getroot()
                node = next(n for n in root.iter() if cls in n.get('class', '').split())
                parents = {child: parent for parent in root.iter() for child in parent}
                carrier = node
                for _ in chain:
                    carrier = parents[carrier]
                mutation(root, node, parents, carrier)
                path = Path(directory) / relative
                path.parent.mkdir(parents=True)
                ET.ElementTree(root).write(path, encoding='utf-8')
                with patch.object(audit, 'UI', directory):
                    self.assertFalse(audit._has_parent_clearance(str(path), cls))

    def test_untagging_a_carrier_invalidates_its_child_declarations(self):
        def remove_clearance(root, node, parents, carrier):
            carrier.set('class', ' '.join(c for c in carrier.get('class', '').split()
                                          if c not in (audit.EDGE_CLASS, audit.BLEED_CLASS)))
        self._check_mutated_chain(remove_clearance)

    def test_reparenting_a_control_invalidates_its_declaration(self):
        def move_to_root(root, node, parents, carrier):
            parents[node].remove(node)
            root.append(node)
        self._check_mutated_chain(move_to_root)

    def test_padding_cannot_replace_a_required_margin_or_vice_versa(self):
        def swap_clearance(root, node, parents, carrier):
            classes = carrier.get('class', '').split()
            replacement = audit.BLEED_CLASS if audit.EDGE_CLASS in classes else audit.EDGE_CLASS
            carrier.set('class', ' '.join(
                [c for c in classes if c not in (audit.EDGE_CLASS, audit.BLEED_CLASS)] + [replacement]))
        self._check_mutated_chain(swap_clearance)

    def test_one_protected_instance_cannot_cover_an_unprotected_duplicate(self):
        self._check_mutated_chain(lambda root, node, parents, carrier: root.append(copy.deepcopy(node)))

    def test_root_carriers_must_be_in_the_same_screen(self):
        vault = 'unity/Emberfall/Assets/UI/PackVault/PackVaultScreen.uxml'
        other = 'unity/Emberfall/Assets/UI/Shop/ShopScreen.uxml'
        used = {
            audit.ROOT_CLASS: [(vault, 1, [audit.ROOT_CLASS, 'pack-vault-screen'], 4)],
            'vault-shell': [(vault, 2, ['vault-shell', audit.BLEED_CLASS], 8)],
            'reveal-overlay': [(other, 3, ['reveal-overlay', audit.BLEED_CLASS], 8)],
        }
        with contextlib.redirect_stdout(io.StringIO()):
            findings = audit.pass7_roots(used)
        self.assertEqual(1, len(findings))
        self.assertIn('reveal-overlay', findings[0])

    def test_tear_effect_declarations_match_the_pack_local_parenting(self):
        source = (REPO_ROOT / audit.SCRIPTS / 'Presentation/PackTearAnimator.cs').read_text(encoding='utf-8')
        for child in ('burst', 'top', 'bottom'):
            self.assertIn(f'face.Add({child});', source)
            self.assertIn(f'{child}.pickingMode = PickingMode.Ignore;', source)

    def test_vault_tooltip_declaration_matches_pack_local_parenting(self):
        source = (REPO_ROOT / audit.SCRIPTS / 'Presentation/PackVaultController.cs').read_text(encoding='utf-8')
        self.assertIn('vault-pack__tooltip', audit.ELEMENT_INTERNAL)
        self.assertIn('tooltip.AddToClassList("vault-pack__tooltip");', source)
        self.assertIn('tile.Add(tooltip);', source)
        self.assertIn('tooltip.pickingMode = PickingMode.Ignore;', source)


if __name__ == '__main__':
    unittest.main()
