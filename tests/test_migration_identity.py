"""Retained migration identities are a release compatibility boundary."""
import hashlib
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class MigrationIdentityChecks(unittest.TestCase):
    def test_reviewed_prefix_and_separate_executor_migrations_are_immutable(self):
        paths = sorted((ROOT / 'db/migrations').glob('*.sql'))
        self.assertEqual([int(p.name[:4]) for p in paths], list(range(1, len(paths) + 1)))
        prefix = ''.join(p.name + ':' + hashlib.sha256(p.read_bytes()).hexdigest() + '\n' for p in paths[:33])
        self.assertEqual(hashlib.sha256(prefix.encode()).hexdigest(), '13379eb2791333d28ebcacd5873f9cfef08b7d6ab1ce7f499996af32d5e7be32')
        retained = {
            '0034_reference_import.sql': 'c0c40d00f2ace033e037e1a335d730de2c680bfaa8b88528339e1e668bd6cdf0',
            '0035_automation_jobs.sql': 'f6b1404978eac822c03dd9e380130843a7e04bfa89c76260d6c55a7da48e3e72',
            '0036_swarm_inventory.sql': '2ab2587bd29ba364fea67c71133c69507d29bfd47f45dfe6df83c5d3b5686474',
            '0037_visibility_lookups.sql': 'ceb7bc542cca52356028032d2d4e35e36ff4d7232f1b8c0b3a7920042df310a7',
            '0038_futures_execution_admission.sql': '4aaa2a249a6050d2e068d8ba030b0964927d080a01ae2179bd0d282e63a305e8',
            '0039_futures_admission_clock.sql': '05fa0e0e196d2b9349ef869c586e3fcff9f4a061c34c2d8adb962b44624fb085',
            '0040_futures_execution_policies.sql': '48a73614a753538b32b0c80c966067f3ec25607973b622a1d540943e648e7d11',
            '0041_reference_read_authority.sql': 'afda6504ead96f4f9f3504c53c81000b3f2ad33080596b0092fbd4fe7ef8dbb6',
            '0042_custom_curves.sql': '2f32c1488f422478edcbb0b4f56117375c37a27c8d602461c23c20d0fe2ba11e',
            '0043_model_providers.sql': 'd39082add6a07e83446fd6adf8bfed87571c9ac20b1125c03cc68e3fd0d81a7f',
        }
        for name, expected in retained.items():
            self.assertEqual(hashlib.sha256((ROOT / 'db/migrations' / name).read_bytes()).hexdigest(), expected, name)
