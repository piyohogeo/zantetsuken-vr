"""Small negative controls for the offline convex gate; no licensed data needed."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('physics_intake', Path(__file__).with_name('Export-Compact16uv-Physics.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class ConvexGateTests(unittest.TestCase):
    def setUp(self):
        self.v = [(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)]
        self.f = [[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]]

    def test_valid(self):
        report = gate.audit_hull(self.v, self.f)
        self.assertEqual(report['edges'], 6)
        self.assertAlmostEqual(report['signedVolume'], 1/6)

    def test_open(self):
        with self.assertRaises(ValueError): gate.audit_hull(self.v, self.f[:-1])

    def test_wrong_winding(self):
        with self.assertRaises(ValueError): gate.audit_hull(self.v, [f[::-1] for f in self.f])

    def test_duplicate_face(self):
        with self.assertRaises(ValueError): gate.audit_hull(self.v, self.f + [self.f[0]])

    def test_nonfinite(self):
        self.v[0] = (float('nan'), 0, 0)
        with self.assertRaises(ValueError): gate.audit_hull(self.v, self.f)

    def test_unused_vertex(self):
        with self.assertRaises(ValueError): gate.audit_hull(self.v + [(.1, .1, .1)], self.f)

    def test_degenerate_face(self):
        self.f[0] = [0, 0, 1]
        with self.assertRaises(ValueError): gate.audit_hull(self.v, self.f)


if __name__ == '__main__': unittest.main()
